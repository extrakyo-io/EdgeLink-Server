using System.Net;
using System.Net.Sockets;

namespace EdgeLink.Sdk.Tests;

/// <summary>
/// 每個測試拿一個沒人在用的埠。
///
/// 寫死埠號在 SDK 測試裡特別危險:這些測試會刻意反覆 Start/Stop、製造半開連線,
/// 前一個測試留下的 TIME_WAIT 或沒收乾淨的 socket 會讓下一個測試以
/// AddressAlreadyInUse 失敗 —— 而那個紅燈與被測的行為毫無關係,只會讓人開始
/// 懷疑測試本身而不是程式碼。
/// </summary>
internal static class PortPool
{
    /// <summary>向 OS 要一個目前空著的埠。</summary>
    public static int Next()
    {
        // 綁 0 讓 OS 挑,拿到號碼後立刻釋放。理論上仍有極小的競爭窗,
        // 但比固定埠號在連續跑測試時穩定得多。
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
