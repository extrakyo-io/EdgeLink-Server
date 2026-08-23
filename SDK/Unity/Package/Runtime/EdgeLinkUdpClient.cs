using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLink
{
    public class EdgeLinkUdpClient : IDisposable
    {
        public event Action<string>?    OnMessage;
        public event Action<Exception>? OnError;
        /// <summary>Fired when an upstream device starts/stops sending packets to EdgeLink Server (timeout-based).
        /// Parameters: isConnected, endpoint (e.g. "UDPPort@192.168.1.50"), deviceId (parsed from message id field).</summary>
        public event Action<bool, string, string>? OnDeviceStatus;

        public int  LocalPort  { get; }
        // 由 Start/Dispose 明確維護,而不是從 cts 推導。先前寫成
        //   !disposed && !cts.IsCancellationRequested
        // 而 cts 是欄位初始化就建好的 —— 剛 new 出來、還沒 Start 的物件會回報 IsRunning=true,
        // 呼叫端拿它判斷「要不要 Start」就會整個跳過啟動。與同專案的 EdgeLinkTcpListener 對齊。
        public bool IsRunning { get; private set; }

        private UdpClient?              udp;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        private bool disposed;

        public EdgeLinkUdpClient(int localPort)
        {
            LocalPort = localPort;
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EdgeLinkUdpClient));
            // 重 Start 時舊 cts/udp 都要先釋放，否則 leak
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            try { udp?.Close(); } catch { }
            try { udp?.Dispose(); } catch { }
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
                    var result = await udp!.ReceiveAsync();
                    string msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (string.IsNullOrEmpty(msg)) continue;

                    if (msg.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal))
                    {
                        // body: "STATUS:protocol@ip" or "STATUS:protocol@ip:deviceId"
                        string body      = msg.Substring(16);
                        int    sep       = body.IndexOf(':');
                        string statusStr = sep >= 0 ? body.Substring(0, sep) : body;
                        string rest      = sep >= 0 ? body.Substring(sep + 1) : "";
                        bool   connected = statusStr.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
                        int    devSep    = rest.LastIndexOf(':');
                        string endpoint  = devSep >= 0 ? rest.Substring(0, devSep)  : rest;
                        string deviceId  = devSep >= 0 ? rest.Substring(devSep + 1) : "";
                        OnDeviceStatus?.Invoke(connected, endpoint, deviceId);
                        continue;
                    }
                    if (msg.StartsWith("EDGELINK_", StringComparison.Ordinal)) continue;

                    queue.Enqueue(msg);
                    OnMessage?.Invoke(msg);   // 先前宣告了事件卻從不觸發,C# 版則有
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

    /// <summary>UDP 送端 —— 只送不收,不綁本機埠。
    /// <para>用來把資料打進 EdgeLink 的 UDP 監聽埠(該埠設定裡的 remotePort)。
    /// UDP 無連線,所以沒有心跳、也沒有「連上了沒」可問。</para></summary>
    public class EdgeLinkUdpSender : IDisposable
    {
        private readonly UdpClient udp = new UdpClient();
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

                // 必須挑與 socket 位址族相容的那一個。udp 是 new UdpClient() 建的,
                // 也就是 IPv4;而 "localhost" 在雙協定的機器上會先解析出 IPv6 的 ::1 ——
                // 拿它去送會直接丟 AddressFamilyNotSupported。
                // 原本的 SendAsync(bytes, len, host, port) 多載內部有處理這件事,
                // 改成自己快取端點之後就得自己挑。
                var family = udp.Client.AddressFamily;
                addr = Array.Find(list, a => a.AddressFamily == family);
                if (addr == null)
                    throw new SocketException((int)SocketError.AddressFamilyNotSupported);
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
