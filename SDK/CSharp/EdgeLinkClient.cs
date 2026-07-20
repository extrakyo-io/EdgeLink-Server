using System;
using System.Collections.Concurrent;
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
        /// Parameters: isConnected, endpoint (e.g. "TCPServer@192.168.1.50"), deviceId (parsed from message id field, may be empty)</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public bool   IsConnected => tcpClient?.Connected == true && !disposed;
        public string Host        { get; }
        public int    Port        { get; }

        private TcpClient?              tcpClient;
        private NetworkStream?          stream;
        private CancellationTokenSource cts = new();
        private readonly ConcurrentQueue<string> queue = new();
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
            cts.Cancel();
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await ConnectCoreAsync(cts.Token);
            _ = Task.Run(() => ReadLoopAsync(cts.Token), cts.Token);
        }

        private async Task ConnectCoreAsync(CancellationToken ct)
        {
            tcpClient?.Dispose();
            tcpClient = new TcpClient { NoDelay = true };
            await tcpClient.ConnectAsync(Host, Port, ct);
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
                        lineBuf.Clear();
                        continue;
                    }

                    int read = await stream!.ReadAsync(buf, 0, buf.Length, ct);
                    if (read == 0)
                    {
                        tcpClient?.Dispose();
                        tcpClient = null;
                        continue;
                    }

                    lineBuf.Append(Encoding.UTF8.GetString(buf, 0, read));

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
                    try { await ConnectCoreAsync(ct); lineBuf.Clear(); } catch { }
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
                string hex = line[14..];
                _ = SendRawAsync($"EDGELINK_PONG:{hex}\n");
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

        public async Task SendAsync(string message)
        {
            if (stream == null || !IsConnected)
                throw new InvalidOperationException("Not connected to EdgeLink.");

            if (!message.EndsWith('\n')) message += "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await stream.WriteAsync(bytes);
        }

        /// <summary>Send raw bytes (e.g. a binary protocol packet) as-is — no newline, no transformation.
        /// For a TCP Server port whose Mask is binary (EdgeLink does the framing + decode).</summary>
        public async Task SendAsync(byte[] data)
        {
            if (stream == null || !IsConnected)
                throw new InvalidOperationException("Not connected to EdgeLink.");
            if (data == null || data.Length == 0) return;
            await stream.WriteAsync(data);
        }

        private async Task SendRawAsync(string raw)
        {
            try
            {
                if (stream == null) return;
                byte[] b = Encoding.UTF8.GetBytes(raw);
                await stream.WriteAsync(b);
            }
            catch { }
        }

        public bool TryDequeue(out string message) => queue.TryDequeue(out message!);

        public void Disconnect()
        {
            cts.Cancel();
            tcpClient?.Dispose();
            tcpClient = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Disconnect();
            cts.Dispose();
        }
    }
}
