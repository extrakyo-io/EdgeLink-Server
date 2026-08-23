using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace EdgeLink.Sdk.Tests;

/// <summary>EdgeLinkUdpClient 的重入與生命週期,以及 EdgeLinkUdpSender。</summary>
public class EdgeLinkUdpTests
{
    private static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (cond()) return true;
            await Task.Delay(20);
        }
        return cond();
    }

    [Fact]
    public void 尚未Start時IsRunning為false()
    {
        // 先前 IsRunning 是從 cts 推導的,而 cts 在欄位初始化就建好 ——
        // 剛 new 出來的物件會回報 true,呼叫端拿它判斷「要不要 Start」就整個跳過啟動。
        using var udp = new EdgeLinkUdpClient(PortPool.Next());
        Assert.False(udp.IsRunning);
    }

    /// <summary>
    /// 重複 Start 先前會直接丟 SocketException:舊的 UdpClient 仍綁著同一個埠,
    /// 而它的參考已經被覆寫掉、收不回來。
    /// </summary>
    [Fact]
    public async Task 重複Start不會丟例外且socket仍在收()
    {
        int port = PortPool.Next();
        using var udp = new EdgeLinkUdpClient(port);

        string received = "";
        using var got = new ManualResetEventSlim(false);
        udp.OnMessage += m => { received = m; got.Set(); };

        udp.Start();
        Assert.True(await WaitUntil(() => udp.IsRunning));

        udp.Start();                       // 這裡先前會炸
        Assert.True(udp.IsRunning, "重複 Start 之後 IsRunning 被舊的接收迴圈清掉了");

        using (var tx = new UdpClient())
        {
            var b = Encoding.UTF8.GetBytes("id:rig1;v:42");
            tx.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        Assert.True(got.Wait(TimeSpan.FromSeconds(3)), "重複 Start 之後 socket 沒在收");
        Assert.Equal("id:rig1;v:42", received);
    }

    [Fact]
    public async Task Dispose之後IsRunning為false且埠已釋放()
    {
        int port = PortPool.Next();
        var udp = new EdgeLinkUdpClient(port);
        udp.Start();
        await WaitUntil(() => udp.IsRunning);

        udp.Dispose();
        Assert.False(udp.IsRunning);

        // 能重新綁 = 舊 socket 真的收乾淨了
        using var probe = new UdpClient(port);
    }

    [Fact]
    public async Task UdpSender送得出去()
    {
        int port = PortPool.Next();
        using var rx = new UdpClient(port);
        using var sender = new EdgeLinkUdpSender();

        await sender.SendAsync("127.0.0.1", port, "id:rig1;v:9");

        rx.Client.ReceiveTimeout = 2000;
        var ep = new IPEndPoint(IPAddress.Any, 0);
        Assert.Equal("id:rig1;v:9", Encoding.UTF8.GetString(rx.Receive(ref ep)));
    }

    /// <summary>
    /// 同一個 host 連送多次不該每次都重新解析 —— UdpClient 的 host 多載會在呼叫端
    /// 執行緒上同步做 DNS,在 Unity 主執行緒上那是每筆都要付的卡頓。
    /// 這裡只驗行為正確(連送多筆都到),快取本身是實作細節。
    /// </summary>
    [Fact]
    public async Task UdpSender連送多筆都會到()
    {
        int port = PortPool.Next();
        using var rx = new UdpClient(port);
        using var sender = new EdgeLinkUdpSender();

        for (int i = 0; i < 5; i++)
            await sender.SendAsync("localhost", port, $"seq:{i}");

        rx.Client.ReceiveTimeout = 2000;
        var seen = new HashSet<string>();
        var ep = new IPEndPoint(IPAddress.Any, 0);
        for (int i = 0; i < 5; i++)
            seen.Add(Encoding.UTF8.GetString(rx.Receive(ref ep)));

        Assert.Equal(5, seen.Count);
    }
}
