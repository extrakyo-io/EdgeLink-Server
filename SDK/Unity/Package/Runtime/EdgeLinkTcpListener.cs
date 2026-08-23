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
        /// Parameters: isConnected, endpoint (e.g. "TCPServer@192.168.1.50:9001")</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public int  LocalPort  { get; }
        public bool IsRunning  { get; private set; }

        private TcpListener?            listener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        // 追蹤所有 Accept 進來的 client，Stop/Dispose 要強制關掉，否則 socket 漏到 OS。
        private readonly ConcurrentDictionary<TcpClient, Conn> _accepted
            = new ConcurrentDictionary<TcpClient, Conn>();
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
            IsRunning = true;
            // 舊 CTS 不再 leak — Stop() 應已 cancel + dispose 它，這裡再保險換新
            try { cts.Dispose(); } catch { }
            cts      = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Any, LocalPort);
            listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(cts.Token));
        }

        // ── 送出 ────────────────────────────────────────────
        // 對端是 EdgeLink(它的 TCP Client 埠連進來),所以這裡的「送」= 回應 EdgeLink。

        /// <summary>目前有幾條連線在。</summary>
        public int ConnectionCount => _accepted.Count;

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
            foreach (var kv in _accepted)
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
                    var client = await listener!.AcceptTcpClientAsync();
                    _accepted.TryAdd(client, new Conn { Client = client });
                    _ = Task.Run(() => ReadLoopAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException)    { return; }   // listener 已 Stop()
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }

        private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
        {
            NetworkStream? networkStream = null;
            try { networkStream = client.GetStream(); }
            catch { _accepted.TryRemove(client, out _); try { client.Dispose(); } catch { } OnDisconnected?.Invoke(); return; }

            // 先把 stream 掛上 Conn,再對外宣告連上 —— 否則 OnConnected 裡呼叫 SendAsync 時
            // conn.Stream 還是 null,那一筆會被靜靜跳過,呼叫端看到的是「已連線卻沒送出去」。
            if (_accepted.TryGetValue(client, out var conn)) conn.Stream = networkStream;
            OnConnected?.Invoke();

            var buf     = new byte[4096];
            var lineBuf = new StringBuilder();
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
            catch (ObjectDisposedException)    { }   // Stop() 主動關掉
            catch (Exception ex) { OnError?.Invoke(ex); }
            finally
            {
                _accepted.TryRemove(client, out _);
                try { networkStream?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
                OnDisconnected?.Invoke();
            }
        }

        private async Task HandleLineAsync(Conn conn, NetworkStream networkStream, string line)
        {
            if (line.StartsWith("EDGELINK_PING:", StringComparison.Ordinal))
            {
                string hex = line.Substring(14);
                // 走同一把鎖 —— 與呼叫端的 SendAsync 交錯會同時毀掉 PONG 的 token
                // (伺服器連續 3 次收不到就斷線)與使用者的訊息。
                await WriteLockedAsync(conn, Encoding.UTF8.GetBytes($"EDGELINK_PONG:{hex}\n"));
                return;
            }
            if (line.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal))
            {
                string body      = line.Substring(16);
                int    sep       = body.IndexOf(':');
                string statusStr = sep >= 0 ? body.Substring(0, sep) : body;
                string rest      = sep >= 0 ? body.Substring(sep + 1) : "";
                bool   connected = statusStr.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
                // rest = "TCPServer@192.168.1.50" or "TCPServer@192.168.1.50:sensor-01"
                int devSep    = rest.LastIndexOf(':');
                string endpoint = devSep >= 0 ? rest.Substring(0, devSep) : rest;
                string deviceId = devSep >= 0 ? rest.Substring(devSep + 1) : "";
                OnDeviceStatus?.Invoke(connected, endpoint, deviceId);
                return;
            }
            if (line.StartsWith("EDGELINK_", StringComparison.Ordinal)) return;

            queue.Enqueue(line);
            OnMessage?.Invoke(line);   // 先前宣告了事件卻從不觸發,C# 版則有
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
            try { cts.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            listener = null;

            // 這裡刻意不 Dispose 每條連線的 WriteLock:SemaphoreSlim.Dispose 不是 thread-safe,
            // 而且不會讓已排隊的 WaitAsync 完成或拋例外 —— 停止當下若有寫入在飛,那個 Task
            // 會永遠停在未完成。沒碰過 AvailableWaitHandle 的 SemaphoreSlim 不持有非托管資源,
            // 交給 GC 即可。
            // 主動關掉所有未斷的 client，避免 socket 殘留到 OS 端
            foreach (var kv in _accepted)
            {
                try { kv.Key.Close(); } catch { }
                try { kv.Key.Dispose(); } catch { }
            }
            _accepted.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            try { cts.Dispose(); } catch { }
        }
    }
}
