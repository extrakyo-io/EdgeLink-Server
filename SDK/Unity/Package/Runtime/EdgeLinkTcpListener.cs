using System;
using System.Collections.Concurrent;
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
                    var client = await listener!.AcceptTcpClientAsync();
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

                    lineBuf.Append(Encoding.UTF8.GetString(buf, 0, read));

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
                string hex = line.Substring(14);
                try
                {
                    byte[] pong = Encoding.UTF8.GetBytes($"EDGELINK_PONG:{hex}\n");
                    await networkStream.WriteAsync(pong, 0, pong.Length);
                }
                catch { }
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
