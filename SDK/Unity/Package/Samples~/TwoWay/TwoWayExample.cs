using UnityEngine;

/// <summary>
/// 雙向用法：收狀態、送命令。
///
/// 前面幾個範例都是純接收；這個示範另一半 —— 把命令送回 EdgeLink，由它依該埠的 Mask
/// 轉成對端要的格式。KV 進、KV 出，二進位協定完全不用你處理。
///
/// 三種 Protocol 都送得出去，差別在對象：
///
///   TCP          連到 EdgeLink 的 TCP Server 埠。送 = 寫進那條連線。
///                Inspector：Tcp Host / Tcp Port
///
///   TCPListener  EdgeLink 的 TCP Client 埠連進來。送 = 回應 EdgeLink，
///                寫給所有已連進來的對端（通常只有一條）。
///                沒有人連進來時 CanSend 是 false。
///
///   UDP          打進 EdgeLink 的 UDP 監聽埠。注意那是設定裡的 remotePort，
///                與收資料的 Udp Local Port 不是同一個 —— 要另外填
///                Inspector 的 Udp Target Host / Udp Target Port。
///
/// 該埠的 Mask 設成二進位時，EdgeLink 會把你送的 KV 編成封包再轉給對端；
/// 設成 OriginalData 時則原樣轉發。兩種情況這支程式寫法完全相同。
/// </summary>
public class TwoWayExample : MonoBehaviour
{
    EdgeLinkManager edgeLink;

    void Start()
    {
        edgeLink = GetComponent<EdgeLinkManager>();

        edgeLink.OnMessage += _ =>
        {
            // 收:對端推來的狀態
            string state = edgeLink.Get("state");
            string pos   = edgeLink.Get("pos");
            Debug.Log($"state={state} pos={pos}");
        };

        edgeLink.OnDeviceStatus += (connected, endpoint, deviceId) =>
            Debug.Log($"{deviceId}@{endpoint} {(connected ? "上線" : "離線")}");
    }

    // 用 Inspector 上的按鈕或你自己的輸入系統呼叫這兩個 —— 範例刻意不用 UnityEngine.Input,
    // 因為專案若把 Active Input Handling 設成 "Input System Package (New)",
    // 舊版 Input 類別會在執行期直接丟例外。

    /// <summary>送一整行。</summary>
    public void SendStart()
    {
        if (!edgeLink.CanSend) { Debug.LogWarning("還沒連上,送不出去"); return; }
        edgeLink.Send("cmd:start");
    }

    /// <summary>送一組欄位,分隔符來自該 mask 的設定。</summary>
    public void SendMove()
    {
        if (!edgeLink.CanSend) { Debug.LogWarning("還沒連上,送不出去"); return; }

        // 浮點一律走 Num() —— 系統地區設定會把小數點變成逗號(例如德文環境的 "1,5"),
        // 而逗號在 KV 裡沒有特殊意義,EdgeLink 會解成不合法的數值把整包丟掉。
        // 這種 bug 只在特定地區的機器上出現,在自己電腦上永遠測不到。
        var p = transform.position;
        edgeLink.Send(
            ("cmd", "move"),
            ("x", EdgeLinkManager.Num(p.x)),
            ("y", EdgeLinkManager.Num(p.y)),
            ("z", EdgeLinkManager.Num(p.z)));
    }

    // 想自己處理送出失敗(例如重試或提示使用者)就用 async 版本。
    // Send() 是 fire-and-forget,失敗只會印一行 warning。
    async void SendCritical()
    {
        try
        {
            await edgeLink.SendAsync("cmd:estop");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"急停送不出去:{ex.Message}");
        }
    }
}
