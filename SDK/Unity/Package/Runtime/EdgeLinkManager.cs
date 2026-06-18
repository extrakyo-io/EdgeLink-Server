using System;
using System.Collections;
using UnityEngine;
using EdgeLink;

/// <summary>
/// EdgeLinkManager — MonoBehaviour 薄殼，內部 delegate 給 <see cref="EdgeLinkBridge"/>。
/// 想用程式碼動態調整 (URL / 參數) 改用 EdgeLinkBridge 建構子；
/// 想拖到 GameObject 上靠 Inspector 設定，繼續用本元件即可。
/// </summary>
public class EdgeLinkManager : MonoBehaviour
{
    public enum Protocol { TCP, TCPListener, UDP }

    [Header("Server")]
    public string   serverUrl = "https://192.168.1.100:8443";
    public string   password  = "";
    public string   maskId    = "OriginalData";

    [Header("連線")]
    public Protocol protocol      = Protocol.TCP;
    public string   tcpHost       = "192.168.1.100";
    public int      tcpPort       = 9001;
    public int      tcpListenPort = 9001;
    public int      udpLocalPort  = 9002;

    [Header("設備偵測")]
    [Tooltip("訊息中代表設備 ID 的欄位名稱，留空則不追蹤 timeout")]
    public string deviceIdKey          = "id";
    [Tooltip("超過幾秒沒收到訊息視為設備離線（0 = 停用）")]
    public float  deviceTimeoutSeconds = 20f;

    [HideInInspector] public string fieldDelimiter = ";";
    [HideInInspector] public string kvSeparator    = ":";

    // ── 狀態 ──────────────────────────────────────────────
    public string Raw            => _bridge?.Raw;
    public string Get(string key) => _bridge?.Get(key);

    /// <summary>底層 bridge — 想取得更細部控制權時用。</summary>
    public EdgeLinkBridge Bridge => _bridge;

    // ── 事件 ──────────────────────────────────────────────
    public event Action<string>                OnMessage;
    public event Action<bool, string, string>  OnDeviceStatus;
    public event Action<string>                OnDeviceTimeout;
    public event Action<string>                OnDeviceReconnected;

    private EdgeLinkBridge _bridge;

    // ── 生命週期 ───────────────────────────────────────────
    private IEnumerator Start()
    {
        _bridge = new EdgeLinkBridge(BuildConfig());

        _bridge.OnMessage           += m => OnMessage?.Invoke(m);
        _bridge.OnDeviceStatus      += (c, ep, id) => OnDeviceStatus?.Invoke(c, ep, id);
        _bridge.OnDeviceTimeout     += id => OnDeviceTimeout?.Invoke(id);
        _bridge.OnDeviceReconnected += id => OnDeviceReconnected?.Invoke(id);

        yield return _bridge.InitializeCoroutine();
    }

    private void Update() => _bridge?.Tick();

    private void OnDestroy()
    {
        _bridge?.Dispose();
        _bridge = null;
    }

    private EdgeLinkBridge.Config BuildConfig() => new EdgeLinkBridge.Config
    {
        ServerUrl            = serverUrl,
        Password             = password,
        MaskId               = maskId,
        Protocol             = (EdgeLinkBridge.Protocol)protocol,
        TcpHost              = tcpHost,
        TcpPort              = tcpPort,
        TcpListenPort        = tcpListenPort,
        UdpLocalPort         = udpLocalPort,
        DeviceIdKey          = deviceIdKey,
        DeviceTimeoutSeconds = deviceTimeoutSeconds,
        FieldDelimiter       = fieldDelimiter,
        KvSeparator          = kvSeparator,
        FetchMaskOnStart     = true,
    };
}
