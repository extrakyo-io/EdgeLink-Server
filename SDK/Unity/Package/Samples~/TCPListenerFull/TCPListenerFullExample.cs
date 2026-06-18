using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 完整範例：多設備資料接收 + 兩種斷線偵測。
///
/// Inspector 設定（EdgeLinkManager）：
///   Protocol        = TCPListener
///   TCP Listen Port = 9001
///   Device Id Key   = "id"
///   Device Timeout  = 20
///
/// 兩種斷線機制：
///   OnDeviceStatus  — TCP 連線層斷線（拔電後約 15 秒，Server PING/PONG 偵測）
///   OnDeviceTimeout — 訊息層沉默（超過 Device Timeout 秒沒資料，任何協定皆適用）
/// </summary>
public class TCPListenerFullExample : MonoBehaviour
{
    EdgeLinkManager edgeLink;

    readonly Dictionary<string, bool> deviceOnline = new();

    void Start()
    {
        edgeLink = GetComponent<EdgeLinkManager>();

        edgeLink.OnMessage += _ =>
        {
            string id = edgeLink.Get("id");
            if (id == null) return;

            if (!deviceOnline.ContainsKey(id))
            {
                deviceOnline[id] = true;
                Debug.Log($"[新裝置] {id} 首次上線");
            }

            Debug.Log($"[資料] {id}  temp={edgeLink.Get("temp")}  humidity={edgeLink.Get("humidity")}");
        };

        // TCP 連線層斷線（deviceId 在收到第一筆資料後才有值）
        edgeLink.OnDeviceStatus += (connected, endpoint, deviceId) =>
        {
            string label = string.IsNullOrEmpty(deviceId)
                ? (endpoint.Contains("@") ? endpoint.Split('@')[1] : endpoint)
                : deviceId;

            if (connected) Debug.Log($"[TCP] {label} 上線");
            else           Debug.LogWarning($"[TCP] {label} 斷線");
        };

        // 訊息層逾時
        edgeLink.OnDeviceTimeout += id =>
        {
            deviceOnline[id] = false;
            Debug.LogWarning($"[Timeout] {id} 超過 {edgeLink.deviceTimeoutSeconds}s 無資料");
        };

        edgeLink.OnDeviceReconnected += id =>
        {
            deviceOnline[id] = true;
            Debug.Log($"[Timeout] {id} 重新上線");
        };
    }
}
