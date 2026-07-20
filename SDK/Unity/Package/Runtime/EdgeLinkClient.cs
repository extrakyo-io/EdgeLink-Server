using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLink
{
    public class EdgeLinkClient : IDisposable
    {
        public event Action<string>?    OnMessage;
        public event Action?            OnConnected;
        public event Action?            OnDisconnected;
        public event Action<Exception>? OnError;
        /// <summary>Fired when an upstream device connects or disconnects from EdgeLink Server.
        /// Parameters: isConnected, protocol, endpoint (e.g. "TCPServer@192.168.1.50:9001")</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public bool IsConnected => tcpClient?.Connected == true && !disposed;
        public string Host { get; }
        public int    Port { get; }

        private TcpClient?              tcpClient;
        private NetworkStream?          stream;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        /// <summary>序列化所有對 stream 的寫入。接收迴圈會在自己的執行緒上回 PONG,
        /// 同時呼叫端可能正在送資料;NetworkStream 不允許併發寫入,一旦交錯會同時毀掉
        /// PONG 的 token(伺服器連續 3 次收不到就主動斷線)與使用者的訊息。</summary>
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        /// <summary>有狀態的 UTF-8 解碼器:保留跨 TCP 讀取邊界的不完整位元組序列。
        /// 每個 chunk 各自 GetString 會把被切開的多位元組字元變成 U+FFFD。</summary>
        private readonly Decoder utf8Decoder = Encoding.UTF8.GetDecoder();
        /// <summary>行緩衝上限。對端若一直不送換行,緩衝會無限成長。</summary>
        private const int MaxLineBufferChars = 64 * 1024;
        private bool disposed;
        private bool autoReconnect    = true;
        private int  reconnectDelayMs = 5000;

        public EdgeLinkClient(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public void SetAutoReconnect(bool enable, int delayMs = 5000)
        {
            autoReconnect    = enable;
            reconnectDelayMs = delayMs;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkClient));
            // 舊 cts 必須 Cancel + Dispose，否則每次 ConnectAsync 都 leak 一個
            var oldCts = cts;
            try { oldCts.Cancel(); } catch { }
            try { oldCts.Dispose(); } catch { }
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await ConnectCoreAsync(cts.Token);
            _ = Task.Run(() => ReadLoopAsync(cts.Token), cts.Token);
        }

        private async Task ConnectCoreAsync(CancellationToken ct)
        {
            // 重連時舊 stream 顯式釋放，不要靠 TcpClient.Dispose 帶
            try { stream?.Dispose(); } catch { }
            stream = null;
            try { tcpClient?.Dispose(); } catch { }
            tcpClient = new TcpClient { NoDelay = true };
            await tcpClient.ConnectAsync(Host, Port);
            stream = tcpClient.GetStream();
            OnConnected?.Invoke();
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buf     = new byte[4096];
            var lineBuf = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (stream == null || !IsConnected)
                    {
                        OnDisconnected?.Invoke();
                        if (!autoReconnect) return;
                        await Task.Delay(reconnectDelayMs, ct);
                        await ConnectCoreAsync(ct);
                        // 連線可能斷在多位元組字元中間,decoder 內會留著孤兒續位元組。
                        // 不重置的話,新連線第一個 chunk 會被接在它後面 → 第一行開頭多一個 U+FFFD。
                        lineBuf.Clear(); utf8Decoder.Reset();
                        continue;
                    }

                    int read = await stream!.ReadAsync(buf, 0, buf.Length, ct);
                    if (read == 0)
                    {
                        tcpClient?.Dispose();
                        tcpClient = null;
                        continue;
                    }

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
                        if (line.Length > 0) HandleLine(line);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex);
                    tcpClient?.Dispose();
                    tcpClient = null;
                    if (!autoReconnect) return;
                    OnDisconnected?.Invoke();
                    try { await Task.Delay(reconnectDelayMs, ct); } catch { return; }
                    try { await ConnectCoreAsync(ct); lineBuf.Clear(); utf8Decoder.Reset(); } catch { }
                }
            }
        }

        private static int FindNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == '\n') return i;
            return -1;
        }

        private void HandleLine(string line)
        {
            if (line.StartsWith("EDGELINK_PING:", StringComparison.Ordinal))
            {
                string hex = line.Substring(14);
                _ = SendRawAsync($"EDGELINK_PONG:{hex}\n");
                return;
            }
            if (line.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal))
            {
                string body      = line.Substring(16);
                int    sep       = body.IndexOf(':');
                string statusStr = sep >= 0 ? body.Substring(0, sep) : body;
                string rest      = sep >= 0 ? body.Substring(sep + 1) : "";
                bool   connected = statusStr.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
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

        public async Task SendAsync(string message)
        {
            if (stream == null || !IsConnected)
                throw new InvalidOperationException("Not connected to EdgeLink.");

            if (!message.EndsWith("\n")) message += "\n";
            await WriteLockedAsync(Encoding.UTF8.GetBytes(message));
        }

        /// <summary>送原始位元組(如二進位協定封包)。不附加換行、不做任何轉換。
        /// 用於「該埠 Mask 為 binary」的 TCP Server 埠(EdgeLink 會做 framing + 解碼)。</summary>
        public async Task SendAsync(byte[] data)
        {
            if (stream == null || !IsConnected)
                throw new InvalidOperationException("Not connected to EdgeLink.");
            if (data == null || data.Length == 0) return;
            await WriteLockedAsync(data);
        }

        private async Task SendRawAsync(string raw)
        {
            try
            {
                if (stream == null) return;
                await WriteLockedAsync(Encoding.UTF8.GetBytes(raw));
            }
            catch { }
        }

        /// <summary>所有寫入都走這裡,確保接收迴圈的 PONG 不會與呼叫端的送出交錯。</summary>
        private async Task WriteLockedAsync(byte[] bytes)
        {
            await writeLock.WaitAsync();
            try
            {
                var s = stream;
                if (s == null) return;
                await s.WriteAsync(bytes, 0, bytes.Length);
            }
            finally { writeLock.Release(); }
        }

        public bool TryDequeue(out string message) => queue.TryDequeue(out message!);

        public void Disconnect()
        {
            try { cts.Cancel(); } catch { }
            try { stream?.Dispose(); } catch { }
            stream = null;
            try { tcpClient?.Dispose(); } catch { }
            tcpClient = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Disconnect();
            try { cts.Dispose(); } catch { }
            try { writeLock.Dispose(); } catch { }
        }
    }
}
