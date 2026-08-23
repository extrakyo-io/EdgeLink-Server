using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    public string   serverUrl = "http://192.168.1.100:8081";
    public string   password  = "";
    public string   maskId    = "OriginalData";

    [Header("連線")]
    public Protocol protocol      = Protocol.TCP;
    public string   tcpHost       = "192.168.1.100";
    public int      tcpPort       = 9001;
    public int      tcpListenPort = 9001;
    public int      udpLocalPort  = 9002;

    [Header("UDP 送出目的地（要送資料才需要）")]
    [Tooltip("EdgeLink 該 UDP 埠的「監聽埠」，與上面收資料的 Udp Local Port 不是同一個。留空表示只收不送。")]
    public string udpTargetHost = "";
    public int    udpTargetPort = 0;

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

    // ── 送出 ──────────────────────────────────────────────
    // 三種 Protocol 都送得出去。UDP 要另外填 Udp Target Host / Port
    // (EdgeLink 該埠的「監聽埠」,與收資料的 Local Port 不是同一個)。

    /// <summary>現在送得出去嗎。</summary>
    public bool CanSend => _bridge != null && _bridge.CanSend;

    /// <summary>送一行 KV 並在失敗時記 log。
    /// <para>MonoBehaviour 裡多半不想為了送一行資料把方法改成 async —— 而 async void
    /// 會把例外吞掉,連線斷了都不會有人發現。這個版本替你把錯誤印出來。</para></summary>
    public void Send(string kvLine)
    {
        _ = SendAndLogAsync(kvLine);
    }

    /// <summary>送一組欄位並在失敗時記 log。分隔符用 Inspector 上設定的那組。</summary>
    public void Send(params (string key, string value)[] fields)
    {
        // 整段包進 try:BuildLine 現在會對含分隔符的值丟例外,而這個多載的契約是
        // 「失敗只 log」—— 讓例外從這裡逃出去會違背它,呼叫端也不會預期要 catch。
        try
        {
            if (fields == null || fields.Length == 0)
                throw new ArgumentException("沒有任何欄位可送");
            if (_bridge == null)
                throw new InvalidOperationException("EdgeLinkManager 尚未初始化");

            var list = new List<KeyValuePair<string, string>>(fields.Length);
            foreach (var f in fields) list.Add(new KeyValuePair<string, string>(f.key, f.value));
            _ = SendAndLogAsync(_bridge.BuildLine(list));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EdgeLink] 送出失敗:{ex.Message}");
        }
    }

    /// <summary>送一行 KV。要自己處理失敗就用這個。</summary>
    public Task SendAsync(string kvLine)
    {
        if (_bridge == null) throw new InvalidOperationException("EdgeLinkManager 尚未初始化");
        return _bridge.SendAsync(kvLine);
    }

    /// <summary>數值轉字串,固定 InvariantCulture —— 系統地區設定會把小數點變成逗號,
    /// EdgeLink 會把那樣的值當成不合法而丟掉整包。</summary>
    public static string Num(float value) => EdgeLinkBridge.Num(value);

    /// <summary>同上,double 版本。</summary>
    public static string Num(double value) => EdgeLinkBridge.Num(value);

    /// <summary>整數版本 —— 沒有它的話 Num(someInt) 會靜默走 float,超過 2^24 的值會被改掉。</summary>
    public static string Num(long value) => EdgeLinkBridge.Num(value);

    private async Task SendAndLogAsync(string kvLine)
    {
        try
        {
            if (kvLine == null) throw new InvalidOperationException("EdgeLinkManager 尚未初始化");
            await SendAsync(kvLine);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EdgeLink] 送出失敗:{ex.Message}");
        }
    }

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
        UdpTargetHost        = udpTargetHost,
        UdpTargetPort        = udpTargetPort,
        DeviceIdKey          = deviceIdKey,
        DeviceTimeoutSeconds = deviceTimeoutSeconds,
        FieldDelimiter       = fieldDelimiter,
        KvSeparator          = kvSeparator,
        FetchMaskOnStart     = true,
    };
}
