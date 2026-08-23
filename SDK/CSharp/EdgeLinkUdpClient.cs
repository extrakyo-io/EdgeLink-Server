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
        // 由 Start/Dispose 明確維護,而不是從 cts 推導。先前寫成
        //   !disposed && !cts.IsCancellationRequested
        // 而 cts 是欄位初始化就建好的 —— 剛 new 出來、還沒 Start 的物件會回報 IsRunning=true,
        // 呼叫端拿它判斷「要不要 Start」就會整個跳過啟動。與同專案的 EdgeLinkTcpListener 對齊。
        public bool IsRunning { get; private set; }

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

            // 重新 Start 前必須先收掉舊的。先前直接覆寫 cts 與 udp:舊的 UdpClient 仍然綁著
            // LocalPort、參考卻已經弄丟 —— 第二次 Start 會因為埠被佔著丟 SocketException,
            // 而舊的接收迴圈還對著那個 socket 繼續跑。Unity 版早就有這段,C# 版一直沒補上。
            try { cts.Cancel(); }   catch (ObjectDisposedException) { }
            try { cts.Dispose(); }  catch (ObjectDisposedException) { }
            try { udp?.Close(); }   catch (SocketException) { }
            try { udp?.Dispose(); } catch (SocketException) { }

            var myCts = new CancellationTokenSource();
            cts = myCts;
            udp = new UdpClient(LocalPort);
            IsRunning = true;
            _ = Task.Run(() => ReceiveLoopAsync(myCts, myCts.Token), myCts.Token);
        }

        // 只有「還是目前這一代」的迴圈才能把 IsRunning 歸位。重新 Start 時舊迴圈正在收尾,
        // 讓它無條件寫 false 會把新一代剛設好的旗標蓋掉 —— 看起來就是 Start 完卻不在跑。
        private void MarkStopped(CancellationTokenSource owner)
        {
            if (ReferenceEquals(cts, owner)) IsRunning = false;
        }

        private async Task ReceiveLoopAsync(CancellationTokenSource owner, CancellationToken ct)
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
                catch (OperationCanceledException) { MarkStopped(owner); return; }
                catch (ObjectDisposedException)    { MarkStopped(owner); return; }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
            MarkStopped(owner);
        }

        public bool TryDequeue(out string message) => queue.TryDequeue(out message!);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsRunning = false;
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
            return SendAsync(Resolve(host, port), message);
        }

        // host 字串 → 解析過的端點。UdpClient.SendAsync(bytes,len,host,port) 內部會在
        // 呼叫端的執行緒上同步做 Dns.GetHostAddresses;在 Unity 主執行緒上那是可見的卡頓,
        // 而且每一筆送出都要付一次。解析結果快取起來,主機沒換就不用重解。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPEndPoint> _resolved
            = new System.Collections.Concurrent.ConcurrentDictionary<string, IPEndPoint>();

        private IPEndPoint Resolve(string host, int port)
        {
            string key = host + ":" + port;
            if (_resolved.TryGetValue(key, out var ep)) return ep;

            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr))
            {
                var list = Dns.GetHostAddresses(host);
                if (list == null || list.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);
                addr = list[0];
            }
            ep = new IPEndPoint(addr, port);
            _resolved[key] = ep;
            return ep;
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
