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

                    EnqueueBounded(msg);
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
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IPEndPoint Ep, DateTime At)> _resolved
            = new System.Collections.Concurrent.ConcurrentDictionary<string, (IPEndPoint, DateTime)>();

        /// <summary>解析結果的保留時間。設 0 表示每次都重新解析。
        /// <para>沒有這個上限的話,對端換 IP(DHCP 續約、伺服器搬機)之後就會永遠打舊位址,
        /// 直到整個程式重啟為止 —— 而且畫面上看不出任何異常,UDP 送出本來就沒有回報。</para></summary>
        public TimeSpan ResolveCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

        private IPEndPoint Resolve(string host, int port)
        {
            string key = host + ":" + port;
            if (_resolved.TryGetValue(key, out var hit) &&
                ResolveCacheTtl > TimeSpan.Zero &&
                DateTime.UtcNow - hit.At < ResolveCacheTtl)
                return hit.Ep;

            IPEndPoint ep;

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
            _resolved[key] = (ep, DateTime.UtcNow);
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
