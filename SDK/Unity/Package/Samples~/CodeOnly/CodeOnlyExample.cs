using System.Collections;
using UnityEngine;
using EdgeLink;

/// <summary>
/// 用程式碼直接建構 EdgeLinkBridge — 不需要把 EdgeLinkManager 拖到場景上，
/// URL / Host / Port 都從程式碼動態決定（例如從 config 檔讀進來、Lobby 給的 IP 等）。
///
/// 把這支腳本拖到任意 GameObject 即可，不需要 EdgeLinkManager 元件。
/// </summary>
public class CodeOnlyExample : MonoBehaviour
{
    [Tooltip("可以從 PlayerPrefs / Lobby / config.json 動態取得")]
    public string serverUrl = "https://192.168.1.100:8443";
    public string tcpHost   = "192.168.1.100";
    public int    tcpPort   = 9001;

    private EdgeLinkBridge _bridge;

    private IEnumerator Start()
    {
        // 最短建構子：URL + Host + Port 帶進來
        _bridge = new EdgeLinkBridge(serverUrl, tcpHost, tcpPort);

        // 或者完整 Config (TCPListener / UDP / 自訂 mask / device timeout ...)：
        // _bridge = new EdgeLinkBridge(new EdgeLinkBridge.Config
        // {
        //     ServerUrl            = serverUrl,
        //     Protocol             = EdgeLinkBridge.Protocol.TCPListener,
        //     TcpListenPort        = 9001,
        //     MaskId               = "OriginalData",
        //     DeviceIdKey          = "id",
        //     DeviceTimeoutSeconds = 15,
        // });

        _bridge.OnMessage += msg =>
        {
            string id    = _bridge.Get("id");
            string temp  = _bridge.Get("temp");
            Debug.Log($"[EdgeLink] id={id} temp={temp} | raw={msg}");
        };

        _bridge.OnDeviceStatus += (connected, endpoint, deviceId) =>
        {
            Debug.Log($"[EdgeLink] device {deviceId} @ {endpoint} → {(connected ? "ONLINE" : "OFFLINE")}");
        };

        _bridge.OnDeviceTimeout     += id => Debug.LogWarning($"[EdgeLink] {id} timeout");
        _bridge.OnDeviceReconnected += id => Debug.Log($"[EdgeLink] {id} reconnected");

        // 拉 mask + 建立連線（Coroutine）
        yield return _bridge.InitializeCoroutine();
    }

    private void Update()
    {
        _bridge?.Tick();  // 每幀 pump 一次 — 訊息事件才會在主執行緒觸發
    }

    private void OnDestroy()
    {
        _bridge?.Dispose();
    }
}
