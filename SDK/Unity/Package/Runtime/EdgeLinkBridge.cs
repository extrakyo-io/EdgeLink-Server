using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace EdgeLink
{
    /// <summary>
    /// EdgeLink 連線控制器（純 C# 類別，不繼承 MonoBehaviour）。
    /// 透過建構子帶入設定，由呼叫端控制生命週期：
    ///   var bridge = new EdgeLinkBridge(new EdgeLinkBridge.Config { ServerUrl = "https://...", ... });
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
            public string   ServerUrl            = "https://192.168.1.100:8443";
            public string   Password             = "";
            public string   MaskId               = "OriginalData";
            public Protocol Protocol             = Protocol.TCP;
            public string   TcpHost              = "192.168.1.100";
            public int      TcpPort              = 9001;
            public int      TcpListenPort        = 9001;
            public int      UdpLocalPort         = 9002;
            public string   DeviceIdKey          = "id";
            public float    DeviceTimeoutSeconds = 20f;
            public string   FieldDelimiter       = ";";
            public string   KvSeparator          = ":";
            public bool     FetchMaskOnStart     = true;
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
        public string Get(string key) => _latest.TryGetValue(key, out var v) ? v : null;

        // ── 事件 ────────────────────────────────────────────
        /// <summary>每筆新訊息到達時觸發（已於主執行緒）。</summary>
        public event Action<string> OnMessage;
        /// <summary>上游裝置 TCP 連線/斷線時觸發 (connected, endpoint, deviceId)。</summary>
        public event Action<bool, string, string> OnDeviceStatus;
        /// <summary>裝置超過 DeviceTimeoutSeconds 沒送資料時觸發。</summary>
        public event Action<string> OnDeviceTimeout;
        /// <summary>逾時的裝置重新送資料時觸發。</summary>
        public event Action<string> OnDeviceReconnected;

        // ── 內部 ────────────────────────────────────────────
        private EdgeLinkClient      _tcp;
        private EdgeLinkTcpListener _tcpListener;
        private EdgeLinkUdpClient   _udp;

        private readonly Dictionary<string, string> _latest       = new Dictionary<string, string>();
        private readonly Dictionary<string, float>  _lastSeenTime = new Dictionary<string, float>();
        private readonly HashSet<string>            _timedOut     = new HashSet<string>();
        private readonly ConcurrentQueue<(bool, string, string)> _deviceStatusQ
            = new ConcurrentQueue<(bool, string, string)>();

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
            if (_tcp         != null) while (_tcp.TryDequeue(out var m))         Handle(m);
            if (_tcpListener != null) while (_tcpListener.TryDequeue(out var m)) Handle(m);
            if (_udp         != null) while (_udp.TryDequeue(out var m))         Handle(m);

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
            _tcp?.Dispose();
            _tcpListener?.Dispose();
            _udp?.Dispose();
            _tcp = null;
            _tcpListener = null;
            _udp = null;
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
                loginReq.certificateHandler = new BypassCertificate();
                yield return loginReq.SendWebRequest();
                if (loginReq.result != UnityWebRequest.Result.Success) yield break;

                string cookie = loginReq.GetResponseHeader("Set-Cookie")?.Split(';')[0] ?? "";

                using (var maskReq = UnityWebRequest.Get($"{baseUrl}/api/masks/{Uri.EscapeDataString(_config.MaskId)}"))
                {
                    maskReq.SetRequestHeader("Cookie", cookie);
                    maskReq.certificateHandler = new BypassCertificate();
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

        private async void Connect()
        {
            switch (_config.Protocol)
            {
                case Protocol.TCP:
                    _tcp = new EdgeLinkClient(_config.TcpHost, _config.TcpPort);
                    _tcp.OnConnected    += () => Debug.Log("[EdgeLink TCP] Connected");
                    _tcp.OnDisconnected += () => Debug.Log("[EdgeLink TCP] Disconnected");
                    _tcp.OnError        += ex => Debug.LogWarning($"[EdgeLink TCP] {ex.Message}");
                    _tcp.OnDeviceStatus += (c, ep, id) => _deviceStatusQ.Enqueue((c, ep, id));
                    _tcp.SetAutoReconnect(true, 5000);
                    try   { await _tcp.ConnectAsync(); }
                    catch { Debug.LogWarning("[EdgeLink TCP] 初始連線失敗，將自動重試"); }
                    break;

                case Protocol.TCPListener:
                    _tcpListener = new EdgeLinkTcpListener(_config.TcpListenPort);
                    _tcpListener.OnConnected    += () => Debug.Log("[EdgeLink TCPListener] EdgeLink connected");
                    _tcpListener.OnDisconnected += () => Debug.Log("[EdgeLink TCPListener] EdgeLink disconnected");
                    _tcpListener.OnError        += ex => Debug.LogWarning($"[EdgeLink TCPListener] {ex.Message}");
                    _tcpListener.OnDeviceStatus += (c, ep, id) => _deviceStatusQ.Enqueue((c, ep, id));
                    _tcpListener.Start();
                    Debug.Log($"[EdgeLink TCPListener] Listening on port {_config.TcpListenPort}");
                    break;

                case Protocol.UDP:
                    _udp = new EdgeLinkUdpClient(_config.UdpLocalPort);
                    _udp.OnError        += ex => Debug.LogWarning($"[EdgeLink UDP] {ex.Message}");
                    _udp.OnDeviceStatus += (c, ep, id) => _deviceStatusQ.Enqueue((c, ep, id));
                    _udp.Start();
                    Debug.Log($"[EdgeLink UDP] Listening on port {_config.UdpLocalPort}");
                    break;
            }
        }

        // ── 訊息處理 ────────────────────────────────────────

        private void Handle(string msg)
        {
            Raw = msg;
            var parsed = Parse(msg);
            foreach (var kv in parsed) _latest[kv.Key] = kv.Value;
            OnMessage?.Invoke(msg);

            if (!string.IsNullOrEmpty(_config.DeviceIdKey) &&
                parsed.TryGetValue(_config.DeviceIdKey, out var deviceId))
            {
                _lastSeenTime[deviceId] = Time.time;
                if (_timedOut.Remove(deviceId))
                    OnDeviceReconnected?.Invoke(deviceId);
            }
        }

        private void CheckTimeouts()
        {
            if (_config.DeviceTimeoutSeconds <= 0 || string.IsNullOrEmpty(_config.DeviceIdKey)) return;
            foreach (var kv in _lastSeenTime)
            {
                if (Time.time - kv.Value > _config.DeviceTimeoutSeconds && _timedOut.Add(kv.Key))
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

        private class BypassCertificate : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

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
