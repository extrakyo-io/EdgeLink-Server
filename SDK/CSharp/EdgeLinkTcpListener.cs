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
        /// <summary>有狀態的 UTF-8 解碼器:保留跨 TCP 讀取邊界的不完整位元組序列。
        /// 每個 chunk 各自 GetString 會把被切開的多位元組字元變成 U+FFFD。</summary>
        private readonly Decoder utf8Decoder = Encoding.UTF8.GetDecoder();
        /// <summary>行緩衝上限。對端若一直不送換行,緩衝會無限成長。</summary>
        private const int MaxLineBufferChars = 64 * 1024;
        private bool disposed;

        public EdgeLinkTcpListener(int localPort)
        {
            LocalPort = localPort;
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkTcpListener));
            if (IsRunning) return;
            IsRunning = true;
            cts      = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Any, LocalPort);
            listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener!.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => ReadLoopAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }

        private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
        {
            OnConnected?.Invoke();
            var networkStream = client.GetStream();
            var buf           = new byte[4096];
            var lineBuf       = new StringBuilder();

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
                        if (line.Length > 0) await HandleLineAsync(networkStream, line);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { OnError?.Invoke(ex); }
            finally
            {
                client.Dispose();
                OnDisconnected?.Invoke();
            }
        }

        private async Task HandleLineAsync(NetworkStream networkStream, string line)
        {
            if (line.StartsWith("EDGELINK_PING:", StringComparison.Ordinal))
            {
                string hex = line[14..];
                try
                {
                    byte[] pong = Encoding.UTF8.GetBytes($"EDGELINK_PONG:{hex}\n");
                    await networkStream.WriteAsync(pong);
                }
                catch { }
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

            queue.Enqueue(line);
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
            cts.Cancel();
            listener?.Stop();
            IsRunning = false;
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
