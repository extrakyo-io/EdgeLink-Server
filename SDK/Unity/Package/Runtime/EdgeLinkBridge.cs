using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace EdgeLink
{
    /// <summary>
    /// EdgeLink 連線控制器（純 C# 類別，不繼承 MonoBehaviour）。
    /// 透過建構子帶入設定，由呼叫端控制生命週期：
    ///   var bridge = new EdgeLinkBridge(new EdgeLinkBridge.Config { ServerUrl = "http://...", ... });
    ///   bridge.OnMessage += msg => Debug.Log(msg);
    ///   StartCoroutine(bridge.InitializeCoroutine());   // 每幀:  bridge.Tick();
    ///   OnDestroy:  bridge.Dispose();
    /// </summary>
    public class EdgeLinkBridge : IDisposable
    {
        public enum Protocol { TCP, TCPListener, UDP }

        // ── 設定 ────────────────────────────────────────────
        [Serializable]
        public class Config
        {
            public string   ServerUrl            = "http://192.168.1.100:8081";
            public string   Password             = "";
            public string   MaskId               = "OriginalData";
            public Protocol Protocol             = Protocol.TCP;
            public string   TcpHost              = "192.168.1.100";
            public int      TcpPort              = 9001;
            public int      TcpListenPort        = 9001;
            public int      UdpLocalPort         = 9002;
            // UDP 送出的目的地 = EdgeLink 該埠的「監聽埠」(設定裡的 remotePort),
            // 與上面收的 UdpLocalPort 不是同一個。留空/0 表示這條只收不送。
            public string   UdpTargetHost        = "";
            public int      UdpTargetPort        = 0;
            public string   DeviceIdKey          = "id";
            public float    DeviceTimeoutSeconds = 20f;
            public string   FieldDelimiter       = ";";
            public string   KvSeparator          = ":";
            public bool     FetchMaskOnStart     = true;

            /// <summary>單一幀最多處理幾筆訊息。0 = 不設限。
            /// <para>Tick() 原本會把佇列抽乾才返回。元件被 disable、場景載入卡住之後
            /// 恢復的那一幀,會一次處理完整個 backlog —— 100 Hz 停 30 秒就是單一幀處理
            /// 3000 筆並觸發 3000 次 OnMessage。預設值對正常流量有兩個數量級的餘裕,
            /// 只在異常堆積時才會生效。</para></summary>
            public int      MaxMessagesPerTick   = 500;

            /// <summary>接收佇列上限,滿了丟最舊的。0 = 不設限。詳見
            /// <see cref="EdgeLinkClient.MaxQueuedMessages"/>。</summary>
            public int      MaxQueuedMessages    = 1000;
        }

        private readonly Config _config;

        public Config Settings => _config;

        // ── 建構子 ──────────────────────────────────────────
        public EdgeLinkBridge(Config config = null)
        {
            _config = config ?? new Config();
        }

        // 便捷建構子 — 最常用情境一行解決
        public EdgeLinkBridge(string serverUrl, string tcpHost, int tcpPort,
                              string maskId = "OriginalData", string password = "")
            : this(new Config
            {
                ServerUrl = serverUrl,
                MaskId    = maskId,
                Password  = password,
                Protocol  = Protocol.TCP,
                TcpHost   = tcpHost,
                TcpPort   = tcpPort,
            })
        { }

        // ── 狀態 ────────────────────────────────────────────
        public string Raw { get; private set; }

        /// <summary>取得最新欄位值(所有裝置混在同一份字典)。
        /// <para>⚠ 多裝置情境請改用 <see cref="Get(string,string)"/>。這個版本以欄位名為唯一的鍵,
        /// 而且不會清掉「本次訊息沒帶到」的欄位 —— 裝置 A 送 <c>id:A;temp:25;humidity:60</c>、
        /// 接著裝置 B 送 <c>id:B;humidity:55</c>(這輪沒回報溫度)之後,讀到的會是
        /// 「B 的溫度 25」,也就是 A 的值被當成 B 的,且沒有任何警示。</para></summary>
        public string Get(string key) => _latest.TryGetValue(key, out var v) ? v : null;

        /// <summary>取得「指定裝置」的最新欄位值 —— 多裝置時請用這個。
        /// deviceId 取自訊息中 <see cref="Config.DeviceIdKey"/> 所指的欄位。</summary>
        public string Get(string deviceId, string key) =>
            deviceId != null && _latestByDevice.TryGetValue(deviceId, out var d) &&
            d.TryGetValue(key, out var v) ? v : null;

        /// <summary>目前看過的裝置 id。</summary>
        public IEnumerable<string> KnownDeviceIds => _latestByDevice.Keys;

        // ── 事件 ────────────────────────────────────────────
        /// <summary>每筆新訊息到達時觸發（已於主執行緒）。</summary>
        public event Action<string> OnMessage;
        /// <summary>上游裝置 TCP 連線/斷線時觸發 (connected, endpoint, deviceId)。</summary>
        public event Action<bool, string, string> OnDeviceStatus;
        /// <summary>裝置超過 DeviceTimeoutSeconds 沒送資料時觸發。</summary>
        public event Action<string> OnDeviceTimeout;
        /// <summary>逾時的裝置重新送資料時觸發。</summary>
        public event Action<string> OnDeviceReconnected;

        /// <summary>連線層錯誤。在主執行緒觸發(與 OnMessage 同一個 Tick)。
        /// <para>沒有訂閱者時錯誤仍會進 Unity Console —— 但要用程式反應(切換備援、
        /// 在 HUD 上顯示斷線)就得靠這個事件。先前只有 Debug.LogWarning,
        /// 而 SendAsync 失敗時丟的例外訊息卻叫人「見 OnError」,指向一個不存在的東西。</para></summary>
        public event Action<Exception> OnError;

        /// <summary>累計因為佇列滿而被丟掉的訊息數。</summary>
        public long DroppedMessageCount =>
            (_tcp?.DroppedMessageCount ?? 0) +
            (_tcpListener?.DroppedMessageCount ?? 0) +
            (_udp?.DroppedMessageCount ?? 0);

        // ── 內部 ────────────────────────────────────────────
        private EdgeLinkClient      _tcp;
        private EdgeLinkTcpListener _tcpListener;
        private EdgeLinkUdpClient   _udp;
        private EdgeLinkUdpSender   _udpSender;

        private readonly Dictionary<string, string> _latest       = new Dictionary<string, string>();
        /// <summary>逐裝置的最新欄位值,供 Get(deviceId, key) 使用。</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _latestByDevice
            = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, float>  _lastSeenTime = new Dictionary<string, float>();
        private readonly HashSet<string>            _timedOut     = new HashSet<string>();
        private readonly ConcurrentQueue<(bool, string, string)> _deviceStatusQ
            = new ConcurrentQueue<(bool, string, string)>();
        // 錯誤同樣要搬到主執行緒才能交給使用者 —— 它們是從背景讀取迴圈來的。
        private readonly ConcurrentQueue<Exception> _errorQ = new ConcurrentQueue<Exception>();
        private long _lastReportedDrops;
        private bool _disposed;

        // ── 啟動 ────────────────────────────────────────────

        /// <summary>給 MonoBehaviour.StartCoroutine() 用 — 拉 mask 後建立連線。</summary>
        public IEnumerator InitializeCoroutine()
        {
            if (_config.FetchMaskOnStart) yield return FetchMaskCoroutine();
            Connect();
        }

        /// <summary>每幀呼叫 — pump 訊息 queue 並檢查 timeout。</summary>
        public void Tick()
        {
            if (_disposed) return;

            int budget = _config.MaxMessagesPerTick > 0 ? _config.MaxMessagesPerTick : int.MaxValue;
            if (_tcp         != null) while (budget > 0 && _tcp.TryDequeue(out var m))         { Handle(m); budget--; }
            if (_tcpListener != null) while (budget > 0 && _tcpListener.TryDequeue(out var m)) { Handle(m); budget--; }
            if (_udp         != null) while (budget > 0 && _udp.TryDequeue(out var m))         { Handle(m); budget--; }

            while (_errorQ.TryDequeue(out var ex)) OnError?.Invoke(ex);

            // 丟棄不該是靜默的。只在數字變動時講一次,免得每幀洗版。
            long drops = DroppedMessageCount;
            if (drops != _lastReportedDrops)
            {
                Debug.LogWarning($"[EdgeLink] 接收佇列滿,已累計丟棄 {drops} 筆 —— " +
                                 "消費端跟不上,或 Tick() 有一段時間沒被呼叫。");
                _lastReportedDrops = drops;
            }

            while (_deviceStatusQ.TryDequeue(out var ds))
            {
                bool connected = ds.Item1;
                string endpoint = ds.Item2;
                string deviceId = ds.Item3;
                if (!connected && !string.IsNullOrEmpty(deviceId))
                {
                    _lastSeenTime.Remove(deviceId);
                    _timedOut.Remove(deviceId);
                }
                OnDeviceStatus?.Invoke(connected, endpoint, deviceId);
            }

            CheckTimeouts();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _tcp?.Dispose(); }         catch { } _tcp = null;
            try { _tcpListener?.Dispose(); } catch { } _tcpListener = null;
            try { _udp?.Dispose(); }         catch { } _udp = null;
            try { _udpSender?.Dispose(); }   catch { } _udpSender = null;
        }

        // ── Mask 拉取 ──────────────────────────────────────

        private IEnumerator FetchMaskCoroutine()
        {
            if (string.IsNullOrEmpty(_config.ServerUrl) || string.IsNullOrEmpty(_config.MaskId)) yield break;

            string baseUrl = _config.ServerUrl.TrimEnd('/');

            byte[] body = Encoding.UTF8.GetBytes($"{{\"password\":\"{EscapeJson(_config.Password)}\"}}");
            using (var loginReq = new UnityWebRequest($"{baseUrl}/api/auth/login", "POST"))
            {
                loginReq.uploadHandler   = new UploadHandlerRaw(body);
                loginReq.downloadHandler = new DownloadHandlerBuffer();
                loginReq.SetRequestHeader("Content-Type", "application/json");
                yield return loginReq.SendWebRequest();
                if (loginReq.result != UnityWebRequest.Result.Success) yield break;

                string cookie = loginReq.GetResponseHeader("Set-Cookie")?.Split(';')[0] ?? "";

                using (var maskReq = UnityWebRequest.Get($"{baseUrl}/api/masks/{Uri.EscapeDataString(_config.MaskId)}"))
                {
                    maskReq.SetRequestHeader("Cookie", cookie);
                    yield return maskReq.SendWebRequest();
                    if (maskReq.result != UnityWebRequest.Result.Success) yield break;

                    var def = JsonUtility.FromJson<MaskDefResponse>(maskReq.downloadHandler.text);
                    if (def != null)
                    {
                        if (!string.IsNullOrEmpty(def.fieldDelimiter)) _config.FieldDelimiter = def.fieldDelimiter;
                        if (!string.IsNullOrEmpty(def.kvSeparator))    _config.KvSeparator    = def.kvSeparator;
                        Debug.Log($"[EdgeLink] 遮罩已套用: {_config.MaskId}");
                    }
                }
            }
        }

        // ── 建立連線 ────────────────────────────────────────

        // async void 是不得已 —— 它由 InitializeCoroutine 以 fire-and-forget 呼叫。
        // 代價是逃出去的例外會變成未處理例外,所以每個分支都要自己收乾淨:
        // 埠被占用時 listener.Start() / udp.Start() 會丟 SocketException,
        // 先前 TCPListener 與 UDP 兩個分支完全沒有 try。
        private async void Connect()
        {
            try
            {
                await ConnectCoreAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EdgeLink] 建立連線失敗:{ex.Message}");
                _errorQ.Enqueue(ex);
            }
        }

        private async Task ConnectCoreAsync()
        {
            switch (_config.Protocol)
            {
                case Protocol.TCP:
                    _tcp = new EdgeLinkClient(_config.TcpHost, _config.TcpPort)
                           { MaxQueuedMessages = _config.MaxQueuedMessages };
                    _tcp.OnConnected    += () => Debug.Log("[EdgeLink TCP] Connected");
                    _tcp.OnDisconnected += () => Debug.Log("[EdgeLink TCP] Disconnected");
                    _tcp.OnError        += ex => { Debug.LogWarning($"[EdgeLink TCP] {ex.Message}"); _errorQ.Enqueue(ex); };
                    _tcp.OnDeviceStatus += (c, ep, id) => _deviceStatusQ.Enqueue((c, ep, id));
                    _tcp.SetAutoReconnect(true, 5000);
                    try   { await _tcp.ConnectAsync(); }
                    catch { Debug.LogWarning("[EdgeLink TCP] 初始連線失敗，將自動重試"); }
                    break;

                case Protocol.TCPListener:
                    _tcpListener = new EdgeLinkTcpListener(_config.TcpListenPort)
                                   { MaxQueuedMessages = _config.MaxQueuedMessages };
                    _tcpListener.OnConnected    += () => Debug.Log("[EdgeLink TCPListener] EdgeLink connected");
                    _tcpListener.OnDisconnected += () => Debug.Log("[EdgeLink TCPListener] EdgeLink disconnected");
                    _tcpListener.OnError        += ex => { Debug.LogWarning($"[EdgeLink TCPListener] {ex.Message}"); _errorQ.Enqueue(ex); };
                    _tcpListener.OnDeviceStatus += (c, ep, id) => _deviceStatusQ.Enqueue((c, ep, id));
                    _tcpListener.Start();
                    Debug.Log($"[EdgeLink TCPListener] Listening on port {_config.TcpListenPort}");
                    break;

                case Protocol.UDP:
                    _udp = new EdgeLinkUdpClient(_config.UdpLocalPort)
                           { MaxQueuedMessages = _config.MaxQueuedMessages };
                    _udp.OnError        += ex => { Debug.LogWarning($"[EdgeLink UDP] {ex.Message}"); _errorQ.Enqueue(ex); };
                    _udp.OnDeviceStatus += (c, ep, id) => _deviceStatusQ.Enqueue((c, ep, id));
                    _udp.Start();
                    if (!string.IsNullOrEmpty(_config.UdpTargetHost) && _config.UdpTargetPort > 0)
                        _udpSender = new EdgeLinkUdpSender();
                    Debug.Log($"[EdgeLink UDP] Listening on port {_config.UdpLocalPort}");
                    break;
            }
        }

        // ── 送出 ────────────────────────────────────────────
        //
        // 三種模式都送得出去,對象不同:
        //   TCP          寫進那條對外連線
        //   TCPListener  寫給所有連進來的對端(通常只有 EdgeLink 一條)
        //   UDP          打到 UdpTargetHost:UdpTargetPort —— 注意那是 EdgeLink 該埠的
        //                「監聽埠」,與收資料的 UdpLocalPort 不是同一個
        //
        // 送不出去時一律丟講清楚原因的例外,而不是靜靜地什麼都沒發生。

        /// <summary>現在送得出去嗎。三種模式的判斷不同:
        /// TCP 看連上沒;TCPListener 看有沒有對端連進來;UDP 看有沒有設定目的地
        /// (UDP 無連線,設了就永遠「送得出去」—— 但送到不存在的對象不會有任何錯誤)。</summary>
        public bool CanSend
        {
            get
            {
                switch (_config.Protocol)
                {
                    case Protocol.TCP:         return _tcp != null && _tcp.IsConnected;
                    case Protocol.TCPListener: return _tcpListener != null && _tcpListener.ConnectionCount > 0;
                    case Protocol.UDP:         return _udpSender != null;
                    default:                   return false;
                }
            }
        }

        /// <summary>送一行 KV 文字給 EdgeLink。會自動補換行,呼叫端不必自己加。
        /// <para>該埠的 Mask 是二進位時,EdgeLink 會把這行 KV 編碼成封包再送給對端;
        /// 是文字 Mask 時則原樣轉發。兩種情況呼叫端寫法相同。</para></summary>
        public Task SendAsync(string kvLine)
        {
            RequireSendable();
            switch (_config.Protocol)
            {
                case Protocol.TCP:         return _tcp.SendAsync(kvLine);
                case Protocol.TCPListener: return ThrowIfNotWritten(_tcpListener.SendAsync(kvLine));
                default:                   return _udpSender.SendAsync(
                                               _config.UdpTargetHost, _config.UdpTargetPort, kvLine);
            }
        }

        /// <summary>用設定好的分隔符把欄位組成一行送出。
        /// <para>浮點請先自己轉字串並指定 InvariantCulture —— 系統地區設定會把小數點變成
        /// 逗號,而逗號在 KV 裡沒有特殊意義,EdgeLink 會當成不合法數值把整包丟掉。
        /// <see cref="Num(float)"/> 已經處理好這件事。</para></summary>
        public Task SendAsync(IEnumerable<KeyValuePair<string, string>> fields)
            => SendAsync(BuildLine(fields));

        /// <summary>送原始位元組,不附加換行也不做任何轉換。
        /// <para>用在「該埠 Mask 為二進位、而且你想自己組封包」的情境 —— 一般情況請用
        /// <see cref="SendAsync(string)"/> 讓 EdgeLink 依 Mask 編碼。</para></summary>
        public Task SendRawAsync(byte[] data)
        {
            RequireSendable();
            switch (_config.Protocol)
            {
                case Protocol.TCP:         return _tcp.SendAsync(data);
                case Protocol.TCPListener: return ThrowIfNotWritten(_tcpListener.SendAsync(data));
                default:
                    throw new InvalidOperationException(
                        "UDP 送原始位元組請直接用 EdgeLinkUdpSender —— " +
                        "Bridge 這層的 UDP 送出只處理文字。");
            }
        }

        /// <summary>依目前設定把欄位組成一行(不送出)。除錯或先看一眼要送什麼時用。</summary>
        public string BuildLine(IEnumerable<KeyValuePair<string, string>> fields)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));

            var sb = new StringBuilder();
            foreach (var f in fields)
            {
                // KV 是純文字格式,沒有跳脫機制 —— 值裡混進分隔符就等於多送了幾個欄位,
                // 收端會把它們當成正常資料。與其產生一行看起來正常、意思卻不同的訊息,
                // 不如在這裡就擋下來。
                Reject(f.Key,   "欄位名");
                Reject(f.Value, "欄位值");

                if (sb.Length > 0) sb.Append(_config.FieldDelimiter);
                sb.Append(f.Key).Append(_config.KvSeparator).Append(f.Value);
            }
            return sb.ToString();
        }

        private void Reject(string text, string what)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Contains(_config.FieldDelimiter) || text.Contains(_config.KvSeparator) ||
                text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
                throw new ArgumentException(
                    $"{what} 含有分隔符或換行,會破壞 KV 的結構:'{text}'。" +
                    "KV 沒有跳脫機制,請先自行編碼(例如換成底線或百分號編碼)。");
        }

        /// <summary>數值轉字串,固定用 InvariantCulture。
        /// <para>NaN / Infinity 會直接丟例外而不是輸出 "NaN"/"∞" —— 那種字串送出去之後,
        /// 對端要嘛整包丟棄、要嘛解析成 0,兩種都比在來源端就發現難查得多。</para></summary>
        public static string Num(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException($"不能送出 {value} —— KV 沒有表示 NaN/Infinity 的方式。");
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        /// <summary>同上,double 版本。</summary>
        public static string Num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException($"不能送出 {value} —— KV 沒有表示 NaN/Infinity 的方式。");
            return value.ToString("0.#########", CultureInfo.InvariantCulture);
        }

        /// <summary>整數版本 —— 沒有這個多載的話 Num(someInt) 會靜默走 float,
        /// 超過 2^24 的值就被改掉了(例如序號、時間戳)。</summary>
        public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>把 listener 的「回 false」翻成例外。
        /// <para>EdgeLinkClient 送不出去是丟例外,EdgeLinkTcpListener 是回 false —— 兩種失敗語意
        /// 混在同一個 Task 回傳型別裡的話,呼叫端(尤其是 Manager.Send 的 try/catch)只會看到
        /// TCP 那條的失敗,TCPListener 的失敗完全靜音。這裡統一成例外。</para></summary>
        private static async Task ThrowIfNotWritten(Task<bool> send)
        {
            if (!await send.ConfigureAwait(false))
                throw new InvalidOperationException(
                    "沒有任何連線寫入成功 —— 對端可能剛斷線,或寫入時發生錯誤" +
                    "(細節見 Unity Console,或訂閱 EdgeLinkBridge.OnError)。");
        }

        private void RequireSendable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EdgeLinkBridge));

            switch (_config.Protocol)
            {
                case Protocol.TCP:
                    if (_tcp == null || !_tcp.IsConnected)
                        throw new InvalidOperationException("尚未連上 EdgeLink(或連線已中斷)。先看 CanSend。");
                    break;

                case Protocol.TCPListener:
                    if (_tcpListener == null)
                        throw new InvalidOperationException("listener 尚未啟動。");
                    if (_tcpListener.ConnectionCount == 0)
                        throw new InvalidOperationException(
                            "還沒有對端連進來。TCPListener 模式是 EdgeLink 主動連向這裡," +
                            "沒有連線就沒有可寫回的對象 —— 先看 CanSend。");
                    break;

                case Protocol.UDP:
                    if (_udpSender == null)
                        throw new InvalidOperationException(
                            "UDP 模式要送資料必須設定 UdpTargetHost 與 UdpTargetPort " +
                            "(EdgeLink 該 UDP 埠的監聽埠,與收資料的 UdpLocalPort 不是同一個)。");
                    break;
            }
        }

        // ── 訊息處理 ────────────────────────────────────────

        private void Handle(string msg)
        {
            Raw = msg;
            var parsed = Parse(msg);
            foreach (var kv in parsed) _latest[kv.Key] = kv.Value;

            // 同時依裝置分開存一份,讓 Get(deviceId, key) 不會拿到別台裝置的殘值
            if (!string.IsNullOrEmpty(_config.DeviceIdKey) &&
                parsed.TryGetValue(_config.DeviceIdKey, out var devId) && !string.IsNullOrEmpty(devId))
            {
                if (!_latestByDevice.TryGetValue(devId, out var perDevice))
                    _latestByDevice[devId] = perDevice = new Dictionary<string, string>();
                foreach (var kv in parsed) perDevice[kv.Key] = kv.Value;
            }

            OnMessage?.Invoke(msg);

            if (!string.IsNullOrEmpty(_config.DeviceIdKey) &&
                parsed.TryGetValue(_config.DeviceIdKey, out var deviceId))
            {
                // unscaledTime 而不是 time:「這台實體裝置還活著嗎」是牆鐘問題。
                // Time.time 受 timeScale 影響 —— VR 訓練程式開暫停選單設 timeScale = 0
                // 是標準做法,那一刻起 Time.time 就停住,暫停期間把設備拔掉也不會逾時。
                _lastSeenTime[deviceId] = Time.unscaledTime;
                if (_timedOut.Remove(deviceId))
                    OnDeviceReconnected?.Invoke(deviceId);
            }
        }

        private void CheckTimeouts()
        {
            if (_config.DeviceTimeoutSeconds <= 0 || string.IsNullOrEmpty(_config.DeviceIdKey)) return;
            foreach (var kv in _lastSeenTime)
            {
                if (Time.unscaledTime - kv.Value > _config.DeviceTimeoutSeconds && _timedOut.Add(kv.Key))
                    OnDeviceTimeout?.Invoke(kv.Key);
            }
        }

        private Dictionary<string, string> Parse(string msg)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(_config.FieldDelimiter) || string.IsNullOrEmpty(_config.KvSeparator))
            {
                result["raw"] = msg;
                return result;
            }
            foreach (var part in msg.Split(new[] { _config.FieldDelimiter }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = part.IndexOf(_config.KvSeparator, StringComparison.Ordinal);
                if (i < 0) continue;
                result[part.Substring(0, i).Trim()] = part.Substring(i + _config.KvSeparator.Length).Trim();
            }
            return result;
        }

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

        [Serializable]
        private class MaskDefResponse
        {
            public string maskId;
            public string outputTemplate;
            public string fieldDelimiter;
            public string kvSeparator;
        }
    }
}
