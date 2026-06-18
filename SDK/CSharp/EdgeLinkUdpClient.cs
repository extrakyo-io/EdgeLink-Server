using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLink
{
    /// <summary>UDP receiver — binds to a local port and fires OnMessage for each packet.</summary>
    public class EdgeLinkUdpClient : IDisposable
    {
        public event Action<string>?    OnMessage;
        public event Action<Exception>? OnError;
        /// <summary>Fired when an upstream device starts/stops sending packets to EdgeLink Server (timeout-based).
        /// Parameters: isConnected, endpoint (e.g. "UDPPort@192.168.1.50"), deviceId (parsed from message id field).</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public int  LocalPort { get; }
        public bool IsRunning => !disposed && !cts.IsCancellationRequested;

        private UdpClient?              udp;
        private CancellationTokenSource cts = new();
        private readonly ConcurrentQueue<string> queue = new();
        private bool disposed;

        public EdgeLinkUdpClient(int localPort)
        {
            LocalPort = localPort;
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkUdpClient));
            cts = new CancellationTokenSource();
            udp = new UdpClient(LocalPort);
            _ = Task.Run(() => ReceiveLoopAsync(cts.Token), cts.Token);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udp!.ReceiveAsync(ct);
                    string msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (string.IsNullOrEmpty(msg)) continue;

                    if (msg.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal))
                    {
                        // body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
                        string body      = msg[16..];
                        int    sep       = body.IndexOf(':');
                        string statusStr = sep >= 0 ? body[..sep] : body;
                        string rest      = sep >= 0 ? body[(sep + 1)..] : "";
                        bool   connected = statusStr.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
                        int    devSep    = rest.LastIndexOf(':');
                        string endpoint  = devSep >= 0 ? rest[..devSep]      : rest;
                        string deviceId  = devSep >= 0 ? rest[(devSep + 1)..] : "";
                        OnDeviceStatus?.Invoke(connected, endpoint, deviceId);
                        continue;
                    }
                    if (msg.StartsWith("EDGELINK_", StringComparison.Ordinal)) continue;

                    queue.Enqueue(msg);
                    OnMessage?.Invoke(msg);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException)    { return; }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }

        public bool TryDequeue(out string message) => queue.TryDequeue(out message!);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cts.Cancel();
            udp?.Close();
            udp?.Dispose();
            cts.Dispose();
        }
    }

    /// <summary>UDP sender — send-only, no local port binding required.</summary>
    public class EdgeLinkUdpSender : IDisposable
    {
        private readonly UdpClient udp = new();
        private bool disposed;

        public Task SendAsync(string host, int port, string message)
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkUdpSender));
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            return udp.SendAsync(bytes, bytes.Length, host, port);
        }

        public Task SendAsync(IPEndPoint endpoint, string message)
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkUdpSender));
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            return udp.SendAsync(bytes, bytes.Length, endpoint);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            udp.Dispose();
        }
    }
}
