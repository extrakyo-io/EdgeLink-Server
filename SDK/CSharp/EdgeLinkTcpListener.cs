using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLink
{
    public class EdgeLinkTcpListener : IDisposable
    {
        public event Action<string>?    OnMessage;
        public event Action?            OnConnected;
        public event Action?            OnDisconnected;
        public event Action<Exception>? OnError;
        /// <summary>Fired when an upstream device connects or disconnects from EdgeLink Server.
        /// Parameters: isConnected, endpoint (e.g. "TCPServer@192.168.1.50"), deviceId (parsed from message id field, may be empty)</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public int  LocalPort { get; }
        public bool IsRunning { get; private set; }

        private TcpListener?            listener;
        private CancellationTokenSource cts = new();
        private readonly ConcurrentQueue<string> queue = new();

        /// <summary>
        /// 佇列上限。0 = 不設限。
        ///
        /// 消費端沒有把訊息取走時(Unity 元件被 disable、場景載入、忘了呼叫 Tick),
        /// 背景 socket 照收,佇列會一路長大到記憶體耗盡。滿了就丟**最舊的** ——
        /// 這類串流的舊值本來就沒有價值,而丟新的等於讓消費端永遠停在過去。
        /// </summary>
        public int MaxQueuedMessages { get; set; } = 1000;

        /// <summary>累計因為佇列滿而被丟掉的訊息數。丟棄不該是靜默的。</summary>
        public long DroppedMessageCount => Interlocked.Read(ref droppedCount);

        private long droppedCount;

        /// <summary>入列並在超過上限時丟掉最舊的。</summary>
        private void EnqueueBounded(string line)
        {
            queue.Enqueue(line);
            int cap = MaxQueuedMessages;
            if (cap <= 0) return;
            while (queue.Count > cap && queue.TryDequeue(out _))
                Interlocked.Increment(ref droppedCount);
        }
        /// <summary>所有 accept 進來、還沒斷的 client。Stop/Dispose 必須主動關掉它們 ——
        /// 先前只關 listener,已建立的連線 socket 會一路殘留到 OS 端;反覆 Start/Stop 就是累積。</summary>
        private readonly ConcurrentDictionary<TcpClient, Conn> accepted = new();
        /// <summary>行緩衝上限。對端若一直不送換行,緩衝會無限成長。</summary>
        private const int MaxLineBufferChars = 64 * 1024;
        private bool disposed;

                /// <summary>一條已接受的連線。每條各自一把寫入鎖 —— PONG 由接收迴圈送出,
        /// 呼叫端的 Send 走另一條執行緒,NetworkStream 不允許併發寫入。</summary>
        private sealed class Conn
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1, 1);
        }

        public EdgeLinkTcpListener(int localPort)
        {
            LocalPort = localPort;
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkTcpListener));
            if (IsRunning) return;
            cts      = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Any, LocalPort);
            listener.Start();
            // 綁定成功之後才算 running。先前這行擺在 listener.Start() 之前 ——
            // 埠被占用時 Start() 丟 SocketException,IsRunning 卻已經是 true,
            // 物件從此謊報自己在跑,呼叫端拿它判斷「要不要重啟」就整個跳過。
            IsRunning = true;
            _ = Task.Run(() => AcceptLoopAsync(cts.Token));
        }

        // ── 送出 ────────────────────────────────────────────
        // 對端是 EdgeLink(它的 TCP Client 埠連進來),所以這裡的「送」= 回應 EdgeLink。

        /// <summary>目前有幾條連線在。</summary>
        public int ConnectionCount => accepted.Count;

        /// <summary>送一行 KV 給所有已連線的對端(通常只有 EdgeLink 一條)。
        /// 自動補換行。沒有任何連線時回 false。</summary>
        public async Task<bool> SendAsync(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (!line.EndsWith("\n")) line += "\n";
            return await SendAsync(Encoding.UTF8.GetBytes(line));
        }

        /// <summary>送原始位元組給所有已連線的對端,不附加換行。</summary>
        public async Task<bool> SendAsync(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            bool any = false;
            foreach (var kv in accepted)
                if (await WriteLockedAsync(kv.Value, data)) any = true;
            return any;
        }

        /// <summary>所有寫出都走這裡:呼叫端的 SendAsync 與接收迴圈的 PONG 共用同一把鎖。
        /// NetworkStream 不允許併發寫入,交錯會把兩邊的內容都切爛。</summary>
        private async Task<bool> WriteLockedAsync(Conn conn, byte[] data)
        {
            if (conn == null || conn.Stream == null) return false;
            // ConfigureAwait(false):Unity 主執行緒上有 UnitySynchronizationContext,
            // 不加的話每個 await 都要排回主執行緒才能繼續 —— 等於「握著寫入鎖等下一幀」,
            // 而 PONG 正好也要搶這把鎖,心跳會被使用者的送出餓死。
            try { await conn.WriteLock.WaitAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return false; }   // Stop() 已經收掉這條連線
            try
            {
                await conn.Stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) { OnError?.Invoke(ex); return false; }
            finally { try { conn.WriteLock.Release(); } catch (ObjectDisposedException) { } }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener!.AcceptTcpClientAsync(ct);
                    accepted.TryAdd(client, new Conn { Client = client });
                    // 不要把 token 傳給 Task.Run 的第二個參數:token 若在工作排程前就被取消,
                    // 委派根本不會執行 —— finally 不跑、OnDisconnected 不觸發、資源不回收。
                    // 連線已經登記進 accepted(ConnectionCount 也算了),卻永遠不會回報斷線,
                    // 消費端就留下一條幽靈連線。迴圈本身已經吃這個 token,交給它才會走完清理。
                    _ = Task.Run(() => ReadLoopAsync(client, ct));
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException)    { return; }   // listener 已被 Stop()
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }

        private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
        {
            NetworkStream networkStream;
            try { networkStream = client.GetStream(); }
            catch (Exception)
            {
                // 對方在 accept 後立刻斷線時 GetStream 會拋 —— 不能讓它衝掉整條 accept 迴圈
                accepted.TryRemove(client, out _);
                try { client.Dispose(); } catch (Exception) { }
                OnDisconnected?.Invoke();
                return;
            }

            // 先把 stream 掛上 Conn,再對外宣告連上 —— 否則 OnConnected 裡呼叫 SendAsync 時
            // conn.Stream 還是 null,那一筆會被靜靜跳過,呼叫端看到的是「已連線卻沒送出去」。
            if (accepted.TryGetValue(client, out var conn)) conn.Stream = networkStream;
            OnConnected?.Invoke();

            var buf           = new byte[4096];
            var lineBuf       = new StringBuilder();
            // 有狀態的 UTF-8 解碼器,**每條連線各一份**。
            // Decoder 會保留跨 chunk 的不完整位元組序列,所以它帶狀態、且非 thread-safe。
            // 這裡每個 client 各跑一個 ReadLoopAsync,若共用同一個 Decoder,A 連線殘留的
            // 半個字元會被接到 B 連線的位元組前面解碼 —— 兩邊的訊息互相污染。
            var utf8Decoder   = Encoding.UTF8.GetDecoder();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = await networkStream.ReadAsync(buf, 0, buf.Length, ct);
                    if (read == 0) break;

                    // 有狀態的 Decoder 會保留跨 chunk 的不完整位元組序列。
                    // 先前是每個 chunk 各自 GetString,多位元組字元一旦被 TCP 切開,
                    // 前半會變成 U+FFFD、後半的接續位元組又變成更多 U+FFFD。
                    int charCount = utf8Decoder.GetCharCount(buf, 0, read);
                    if (charCount > 0)
                    {
                        var chars = new char[charCount];
                        utf8Decoder.GetChars(buf, 0, read, chars, 0);
                        lineBuf.Append(chars, 0, charCount);
                    }

                    if (lineBuf.Length > MaxLineBufferChars)
                    {
                        OnError?.Invoke(new InvalidDataException(
                            $"Line buffer exceeded {MaxLineBufferChars} chars without a newline — discarding."));
                        lineBuf.Clear();
                    }

                    int idx;
                    while ((idx = FindNewline(lineBuf)) >= 0)
                    {
                        string line = lineBuf.ToString(0, idx).Trim();
                        lineBuf.Remove(0, idx + 1);
                        if (line.Length > 0) await HandleLineAsync(conn, networkStream, line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnError?.Invoke(ex); }
            finally
            {
                accepted.TryRemove(client, out _);
                try { networkStream.Dispose(); } catch (Exception) { }
                try { client.Dispose(); }        catch (Exception) { }
                OnDisconnected?.Invoke();
            }
        }

        private async Task HandleLineAsync(Conn conn, NetworkStream networkStream, string line)
        {
            if (line.StartsWith("EDGELINK_PING:", StringComparison.Ordinal))
            {
                string hex = line[14..];
                // 走同一把鎖 —— 與呼叫端的 SendAsync 交錯會同時毀掉 PONG 的 token
                // (伺服器連續 3 次收不到就斷線)與使用者的訊息。
                await WriteLockedAsync(conn, Encoding.UTF8.GetBytes($"EDGELINK_PONG:{hex}\n"));
                return;
            }
            if (line.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal))
            {
                // body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
                string body      = line[16..];
                int    sep       = body.IndexOf(':');
                string statusStr = sep >= 0 ? body[..sep] : body;
                string rest      = sep >= 0 ? body[(sep + 1)..] : "";
                bool   connected = statusStr.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
                int    devSep    = rest.LastIndexOf(':');
                string endpoint  = devSep >= 0 ? rest[..devSep]      : rest;
                string deviceId  = devSep >= 0 ? rest[(devSep + 1)..] : "";
                OnDeviceStatus?.Invoke(connected, endpoint, deviceId);
                return;
            }
            if (line.StartsWith("EDGELINK_", StringComparison.Ordinal)) return;

            EnqueueBounded(line);
            OnMessage?.Invoke(line);
        }

        private static int FindNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == '\n') return i;
            return -1;
        }

        public bool TryDequeue(out string message) => queue.TryDequeue(out message!);

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { cts.Cancel(); }    catch (ObjectDisposedException) { }
            try { listener?.Stop(); } catch (SocketException) { }
            listener = null;

            // 這裡刻意不 Dispose 每條連線的 WriteLock:SemaphoreSlim.Dispose 不是 thread-safe,
            // 而且不會讓已排隊的 WaitAsync 完成或拋例外 —— 停止當下若有寫入在飛,那個 Task
            // 會永遠停在未完成。沒碰過 AvailableWaitHandle 的 SemaphoreSlim 不持有非托管資源,
            // 交給 GC 即可。
            // 主動關掉還連著的 client。取消 token 只會讓 ReadLoop 停止讀取,
            // 不會關閉對端的連線 —— 沒有這一段,socket 會殘留到 OS 端。
            foreach (var kv in accepted)
            {
                try { kv.Key.Close(); }   catch (Exception) { }
                try { kv.Key.Dispose(); } catch (Exception) { }
            }
            accepted.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            cts.Dispose();
        }
    }
}
