using UnityEngine;

/// <summary>
/// 最簡單的用法：收到訊息就讀欄位。
///
/// Inspector 設定（EdgeLinkManager）：
///   Protocol        = TCPListener
///   TCP Listen Port = 9001
/// </summary>
public class TCPListenerExample : MonoBehaviour
{
    EdgeLinkManager edgeLink;

    void Start()
    {
        edgeLink = GetComponent<EdgeLinkManager>();

        edgeLink.OnMessage += _ =>
        {
            string id       = edgeLink.Get("id");
            string temp     = edgeLink.Get("temp");
            string humidity = edgeLink.Get("humidity");
            Debug.Log($"{id}  temp={temp}  humidity={humidity}");
        };
    }
}
