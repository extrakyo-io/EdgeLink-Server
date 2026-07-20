using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeLink
{
    /// <summary>
    /// EdgeLink 來源端 TCP SDK —— 給「感測端 / 資料來源」用來把遙測「送進」EdgeLink 的 TCP Server 埠。
    /// （這與 EdgeLinkClient 相反：EdgeLinkClient 偏消費端；本類別是 *送資料進* EdgeLink 的來源端。）
    ///
    /// ★ 最重要：自動回應 EdgeLink 的心跳。
    ///   EdgeLink TCP Server 連上 3 秒後每 5 秒送一個 EDGELINK_PING:{hex}；
    ///   來源端 3 次沒回 EDGELINK_PONG:{同一個 hex}（約 15 秒）就會被伺服器主動斷線。
    ///   本 SDK 在背景收到 PING 會自動回 PONG —— 你只管送資料，不會被心跳踢掉。
    ///
    /// 送法兩種：
    ///   • SendLineAsync(kvText)  ——【推薦】送一行 KV 文字（自動補 '\n'）。
    ///       EdgeLink TCP Server 原生就是「UTF-8 + 換行分隔」收文字，伺服器完全不用改，
    ///       該埠的 Mask 設成 OriginalData（原樣轉發）即可，下游 client 直接收到 KV。
    ///   • SendRawAsync(bytes)    —— 送原始位元組(如原廠 OK 二進位封包)。
    ///       伺服器端該埠 Mask 要設成 binary(BinarySpec),且務必設 sync 對齊 magic(如 "4f4b"='OK')；
    ///       EdgeLink 會用 BinaryStreamFramer 分包、BinaryMaskDecoder 解成 KV 再轉發。
    ///       注意:一個 TCP 埠只跑一種模式 —— 該埠 Mask 是 binary → 只收二進位(且不送心跳,靠 TCP keepalive)；
    ///       否則 → 只收 KV 文字(走 PING/PONG,本 SDK 自動回 PONG)。
    ///
    /// 用法：
    ///   var src = new EdgeLinkSourceClient("127.0.0.1", 9000);
    ///   src.OnConnected    += ()  => Console.WriteLine("EdgeLink connected");
    ///   src.OnDisconnected += ()  => Console.WriteLine("EdgeLink disconnected");
    ///   src.Start();                                   // 連線 + 背景維護(PING→PONG、斷線自動重連)
    ///   // 每算出一組新值就送一行（fire-and-forget）：
    ///   await src.SendLineAsync($"id:rig1;seq:{seq};conn:2;jlx:{jlx:0.###};jly:{jly:0.###};...");
    /// </summary>
    public sealed class EdgeLinkSourceClient : IDisposable
    {
        private const string PingPrefix = "EDGELINK_PING:";
        private const string PongPrefix = "EDGELINK_PONG:";

        public string Host { get; }
        public int    Port { get; }
        public bool   IsConnected => _client != null && _client.Connected;

        /// <summary>連線成功（含自動重連成功）時觸發。</summary>
        public event Action              OnConnected;
        /// <summary>連線中斷時觸發。</summary>
        public event Action              OnDisconnected;
        /// <summary>非致命錯誤（送出失敗、連線例外等）。</summary>
        public event Action<Exception>   OnError;

        private TcpClient               _client;
        private NetworkStream           _stream;
        private CancellationTokenSource _cts;
        private Task                    _loopTask;
        private readonly SemaphoreSlim  _writeLock = new SemaphoreSlim(1, 1);

        private volatile bool _autoReconnect = true;
        private int           _reconnectDelayMs = 3000;
        private bool          _disposed;

        public EdgeLinkSourceClient(string host, int port)
        {
            Host = host;
            Port = port;
        }

        /// <summary>斷線是否自動重連（預設開，延遲 3 秒）。</summary>
        public void SetAutoReconnect(bool enable, int delayMs = 3000)
        {
            _autoReconnect    = enable;
            _reconnectDelayMs = Math.Max(200, delayMs);
        }

        /// <summary>啟動：連線並開始背景維護（收 PING 自動回 PONG、斷線自動重連）。</summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EdgeLinkSourceClient));
            if (_loopTask != null) return;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        // ── 送資料 ──────────────────────────────────────────────────────────

        /// <summary>送一行 KV 文字（自動補換行）。未連線時直接丟棄（遙測 fire-and-forget）。</summary>
        public Task SendLineAsync(string kvLine, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(kvLine)) return Task.CompletedTask;
            if (kvLine[kvLine.Length - 1] != '\n') kvLine += "\n";
            return SendBytesAsync(Encoding.UTF8.GetBytes(kvLine), ct);
        }

        /// <summary>送原始位元組(如 OK 二進位封包)。伺服器端該埠須為 binary Mask 且設 sync 對齊 magic。</summary>
        public Task SendRawAsync(byte[] data, CancellationToken ct = default)
            => (data == null || data.Length == 0) ? Task.CompletedTask : SendBytesAsync(data, ct);

        private async Task SendBytesAsync(byte[] data, CancellationToken ct)
        {
            var stream = _stream;
            if (stream == null || !IsConnected) return;   // 未連線就丟棄，不阻塞來源
            try
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try { await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false); }
                finally { _writeLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { OnError?.Invoke(ex); }
        }

        // ── 連線 / 收 PING 回 PONG / 重連 ─────────────────────────────────────

        private async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(Host, Port).ConfigureAwait(false);
                    _client.NoDelay = true;                 // 低延遲：關 Nagle，別讓遙測被緩衝合併
                    _stream = _client.GetStream();
                    SafeInvoke(OnConnected);
                    await ReceiveUntilClosedAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { OnError?.Invoke(ex); }

                CloseSocket();
                SafeInvoke(OnDisconnected);

                if (!_autoReconnect || ct.IsCancellationRequested) break;
                try { await Task.Delay(_reconnectDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // EdgeLink 回給「來源端」的只有控制訊息（EDGELINK_PING / EDGELINK_STATUS 等），
        // 都是 UTF-8 + 換行的文字行。這裡專責：收到 PING → 立刻回 PONG；其餘忽略。
        private async Task ReceiveUntilClosedAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];
            var acc    = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = await _stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (n <= 0) return;   // 對端關閉連線

                acc.Append(Encoding.UTF8.GetString(buffer, 0, n));

                int nl;
                while ((nl = IndexOf(acc, '\n')) >= 0)
                {
                    string line = acc.ToString(0, nl).Trim();
                    acc.Remove(0, nl + 1);
                    if (line.Length == 0) continue;

                    if (line.StartsWith(PingPrefix, StringComparison.Ordinal))
                    {
                        string token = line.Substring(PingPrefix.Length).Trim();
                        // 原封回 PONG（server 以 hex token 比對 pending ping）
                        await SendBytesAsync(Encoding.ASCII.GetBytes(PongPrefix + token + "\n"), ct)
                            .ConfigureAwait(false);
                    }
                    // 其餘 EDGELINK_* / 任何回傳資料：來源端不需要，忽略
                }

                if (acc.Length > 16384) acc.Clear();   // 保險：對端一直不送換行也不無限膨脹
            }
        }

        private static int IndexOf(StringBuilder sb, char c)
        {
            for (int i = 0; i < sb.Length; i++) if (sb[i] == c) return i;
            return -1;
        }

        private void CloseSocket()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close();   } catch { }
            _stream = null;
            _client = null;
        }

        private void SafeInvoke(Action a)
        {
            try { a?.Invoke(); } catch (Exception ex) { OnError?.Invoke(ex); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed      = true;
            _autoReconnect = false;
            try { _cts?.Cancel(); } catch { }
            CloseSocket();
            try { _cts?.Dispose(); }       catch { }
            try { _writeLock.Dispose(); }  catch { }
        }
    }
}
