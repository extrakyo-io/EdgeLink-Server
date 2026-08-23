using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace EdgeLink.Sdk.Tests;

/// <summary>
/// EdgeLinkTcpListener 的送出與生命週期。全部走真的 socket —— 這個類別的價值就在於
/// 它與 OS 的互動(埠釋放、半開連線、併發寫入),mock 掉就等於什麼都沒測。
/// </summary>
public class EdgeLinkTcpListenerTests : IDisposable
{
    private readonly List<IDisposable> _cleanup = [];

    private EdgeLinkTcpListener Listener(int port)
    {
        var l = new EdgeLinkTcpListener(port);
        _cleanup.Add(l);
        return l;
    }

    private TcpClient Connect(int port)
    {
        var c = new TcpClient();
        c.Connect("127.0.0.1", port);
        _cleanup.Add(c);
        return c;
    }

    public void Dispose()
    {
        foreach (var d in _cleanup)
        {
            try { d.Dispose(); } catch (Exception) { }
        }
    }

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

    // ── 生命週期 ────────────────────────────────────────────────────────────

    [Fact]
    public void 尚未Start時IsRunning為false()
    {
        var listener = Listener(PortPool.Next());
        Assert.False(listener.IsRunning);
        Assert.Equal(0, listener.ConnectionCount);
    }

    [Fact]
    public async Task Stop之後可以再Start()
    {
        int port = PortPool.Next();
        var listener = Listener(port);

        listener.Start();
        Assert.True(await WaitUntil(() => listener.IsRunning));

        listener.Stop();
        Assert.False(listener.IsRunning);

        // 先前 Stop() 只關 listener、不收已接受的連線,埠會被卡住而重啟失敗
        listener.Start();
        Assert.True(await WaitUntil(() => listener.IsRunning), "Stop 之後 Start 不起來 —— 埠沒被釋放");
    }

    /// <summary>
    /// 取消 token 只會讓讀取迴圈停止,不會關閉對端的連線。Stop() 必須主動收掉,
    /// 否則 socket 會殘留到 OS 端,反覆 start/stop 就是累積。
    /// </summary>
    [Fact]
    public async Task Stop會主動關閉已接受的連線()
    {
        int port = PortPool.Next();
        var listener = Listener(port);
        int disconnected = 0;
        listener.OnDisconnected += () => Interlocked.Increment(ref disconnected);
        listener.Start();

        var peers = new[] { Connect(port), Connect(port), Connect(port) };
        Assert.True(await WaitUntil(() => listener.ConnectionCount == 3),
            $"只追蹤到 {listener.ConnectionCount} 條連線");

        listener.Stop();
        Assert.True(await WaitUntil(() => disconnected == 3), $"OnDisconnected 只觸發 {disconnected} 次");

        // 對端讀到 0 bytes 或例外 = server 真的把連線關了
        int closedSeen = 0;
        foreach (var p in peers)
        {
            try
            {
                p.Client.Blocking = true;
                p.ReceiveTimeout = 1000;
                if (p.Client.Receive(new byte[1]) == 0) closedSeen++;
            }
            catch (SocketException)       { closedSeen++; }
            catch (ObjectDisposedException) { closedSeen++; }
        }
        Assert.Equal(3, closedSeen);
    }

    // ── 送出 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 沒有連線時送出回false()
    {
        var listener = Listener(PortPool.Next());
        listener.Start();
        await WaitUntil(() => listener.IsRunning);

        Assert.False(await listener.SendAsync("cmd:noop"));
    }

    [Fact]
    public async Task 送出會寫給每一條連線()
    {
        int port = PortPool.Next();
        var listener = Listener(port);
        listener.Start();

        var peers = new[] { Connect(port), Connect(port) };
        Assert.True(await WaitUntil(() => listener.ConnectionCount == 2));

        Assert.True(await listener.SendAsync("cmd:start;v:1"));

        foreach (var p in peers)
        {
            p.ReceiveTimeout = 2000;
            var buf = new byte[256];
            int n = p.Client.Receive(buf);
            Assert.Equal("cmd:start;v:1", Encoding.UTF8.GetString(buf, 0, n).Trim());
        }
    }

    /// <summary>
    /// 最重要的一條:PONG 與呼叫端的送出必須共用同一把寫入鎖。
    ///
    /// NetworkStream 不允許併發寫入 —— 交錯會**同時**毀掉 PONG 的 token
    /// (伺服器連續 3 次收不到就主動斷線)與使用者的訊息。
    ///
    /// PING 與 Send 必須走**同一條**連線才測得到:先前的驗證把 PING 灌給一條、
    /// 卻去讀另一條,那條沒有 PONG 流量,等於完全避開了要驗的情境。
    /// </summary>
    [Fact]
    public async Task 送出與PONG在同一條連線上不會交錯()
    {
        const int Senders = 8, PerSender = 60;

        int port = PortPool.Next();
        var listener = Listener(port);
        listener.Start();

        var peer = Connect(port);
        Assert.True(await WaitUntil(() => listener.ConnectionCount == 1));
        var stream = peer.GetStream();

        using var stop = new CancellationTokenSource();
        var pinger = Task.Run(async () =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                var b = Encoding.UTF8.GetBytes($"EDGELINK_PING:{i++:x8}\n");
                try { await stream.WriteAsync(b, stop.Token); } catch (Exception) { return; }
                await Task.Delay(2, CancellationToken.None);
            }
        });

        await Task.WhenAll(Enumerable.Range(0, Senders).Select(id => Task.Run(async () =>
        {
            for (int k = 0; k < PerSender; k++) await listener.SendAsync($"cmd:m{id}_{k}");
        })));

        stop.Cancel();
        try { await pinger; } catch (Exception) { }
        await Task.Delay(400);

        var sb = new StringBuilder();
        peer.ReceiveTimeout = 1500;
        try
        {
            var buf = new byte[65536];
            while (true)
            {
                int n = peer.Client.Receive(buf);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
        }
        catch (SocketException) { }

        int cmds = 0, pongs = 0;
        var corrupt = new List<string>();
        foreach (var raw in sb.ToString().Split('\n'))
        {
            string l = raw.Trim();
            if (l.Length == 0) continue;
            if (Regex.IsMatch(l, @"^cmd:m[0-7]_\d+$"))                 cmds++;
            else if (Regex.IsMatch(l, @"^EDGELINK_PONG:[0-9a-f]{8}$")) pongs++;
            else corrupt.Add(l);
        }

        Assert.Equal(Senders * PerSender, cmds);
        Assert.True(corrupt.Count == 0,
            $"有 {corrupt.Count} 行被切爛,例如 '{corrupt.FirstOrDefault()}'(PONG {pongs} 筆)");
    }

    /// <summary>
    /// 連線登記必須早於 OnConnected —— 否則在 handler 裡送出的第一筆(常見的握手訊息)
    /// 會因為 Stream 還沒掛上而被靜靜跳過,呼叫端拿不到任何錯誤訊號。
    /// </summary>
    [Fact]
    public async Task 從OnConnected送出的第一筆不會被丟掉()
    {
        int port = PortPool.Next();
        var listener = Listener(port);

        bool? sendResult = null;
        var sent = new TaskCompletionSource<bool>();
        listener.OnConnected += async () =>
        {
            sendResult = await listener.SendAsync("id:client01;ready:1");
            sent.TrySetResult(true);
        };
        listener.Start();

        var peer = Connect(port);
        await Task.WhenAny(sent.Task, Task.Delay(3000));

        Assert.True(sendResult.HasValue, "OnConnected 的送出沒有完成");
        Assert.True(sendResult!.Value, "OnConnected 裡送出被靜默丟棄 —— Stream 尚未掛上 Conn");

        peer.ReceiveTimeout = 2000;
        var buf = new byte[256];
        int n = peer.Client.Receive(buf);
        Assert.Equal("id:client01;ready:1", Encoding.UTF8.GetString(buf, 0, n).Trim());
    }
}
