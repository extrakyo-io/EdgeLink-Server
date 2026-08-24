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

        // 同步檢查。Stop() 會先 cts.Cancel(),而那會讓 ReadLoop 走進 finally ——
        // 那裡也會 Dispose client 並觸發 OnDisconnected。若只等 disconnected == 3,
        // 測到的是 ReadLoop 的 finally,不是 Stop() 自己那段主動關閉:實測把
        // Stop() 裡的 foreach + accepted.Clear() 整段刪掉,這條測試照樣通過。
        Assert.Equal(0, listener.ConnectionCount);

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
    /// 併發送出 + 持續 PING 的整合檢查:8 條執行緒各送 PerSender 筆 PayloadBytes 大的
    /// 訊息,全部必須原封不動抵達,且不與 PONG 互相切開。
    ///
    /// **這條測試在 Windows 上證明不了寫入鎖是必要的。** 實測:把 WriteLock 整組
    /// 刪掉,它照樣通過 —— Winsock 對同一個 socket 的單次 WSASend 本身就是序列化的,
    /// 所以無論送多大、對端讀多慢,內容都不會被切開。這條測試最早那版每筆只有
    /// 13 bytes,同樣抓不到,只是理由更淺(連分段都不會發生)。
    ///
    /// 保留大 payload 的理由是它在 **Unix** 上有意義:.NET 在那邊會對大緩衝做部分
    /// 寫入並重試,沒有鎖就真的會交錯。CI 若有 Linux runner,這條就會變成有效的守護。
    ///
    /// 鎖本身的依據不是這條測試,而是 NetworkStream 的契約:它明文只支援一個並行讀
    /// 加一個並行寫。PONG 走同一把鎖也是同一個理由 —— 交錯會**同時**毀掉心跳 token
    /// (伺服器連續 3 次收不到就主動斷線)與使用者的訊息。
    /// </summary>
    [Fact]
    public async Task 送出與PONG在同一條連線上不會交錯()
    {
        const int Senders = 4, PerSender = 2, PayloadBytes = 32 * 1024;

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

        // 對端一邊慢慢讀,一邊讓送出端持續被背壓卡住 —— 這才會出現部分寫入
        var sb = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            var buf = new byte[8192];
            while (!stop.IsCancellationRequested)
            {
                int n;
                try { n = await peer.GetStream().ReadAsync(buf, CancellationToken.None); }
                catch (Exception) { return; }
                if (n <= 0) return;
                lock (sb) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
        });

        var senders = Enumerable.Range(0, Senders).Select(id => Task.Run(async () =>
        {
            for (int k = 0; k < PerSender; k++)
            {
                // 每行:cmd:m{id}_{k}: 後面接固定長度的填充,最後 #。任何交錯都會破壞這個結構。
                string head = $"cmd:m{id}_{k}:";
                var line = new StringBuilder(head, PayloadBytes + 2);
                line.Append(new string((char)('a' + id), PayloadBytes - head.Length - 1));
                line.Append('#');
                await listener.SendAsync(line.ToString());
            }
        })).ToArray();

        // 沒有寫入鎖的時候,併發 WriteAsync 不只會切爛內容 —— 它會把 stream 整個卡住
        // (實測:拿掉鎖之後這一段永遠不返回)。掛住是很糟的失敗模式,轉成明確的失敗。
        var sending = Task.WhenAll(senders);
        if (await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(60))) != sending)
        {
            stop.Cancel();
            Assert.Fail("送出在 60 秒內沒有完成 —— 併發寫入把 stream 卡住了");
        }
        await sending;

        // 等到收足預期位元組數為止,不要用固定 sleep:機器忙的時候固定等待會變成
        // 隨機失敗,閒的時候又白等。
        // 等到收足預期位元組數為止,不要用固定 sleep:機器忙的時候固定等待會變成
        // 隨機失敗,閒的時候又白等。
        int expectedChars = Senders * PerSender * (PayloadBytes + 1);   // +1 = 換行
        bool arrived = await WaitUntil(() => { lock (sb) return sb.Length >= expectedChars; }, 30000);
        stop.Cancel();
        try { await pinger; }  catch (Exception) { }
        try { await reading; } catch (Exception) { }

        int got;
        lock (sb) got = sb.Length;

        // 逾時要說自己是逾時。先前這裡直接往下解析,沒收完的最後一行會被當成
        // 「被切爛」回報 —— 在 CI 上就是一條看起來像併發 bug、其實是資料還沒到齊的
        // 假陽性(那次的訊息是「有 1 行被切爛」,但同時 PONG 收到 0 筆,
        // 也就是對端根本沒在讀)。
        Assert.True(arrived,
            $"30 秒內只收到 {got}/{expectedChars} 字元 —— 資料沒有全部抵達,這不是內容損毀");
        int cmds = 0, pongs = 0;
        var corrupt = new List<string>();
        string all;
        lock (sb) all = sb.ToString();

        // 只看完整的行。串流的尾巴本來就可能停在一行的中間,把那半行算成損毀
        // 等於用「還沒讀完」冒充「內容被切爛」。
        var lines = all.Split('\n');
        int complete = all.EndsWith("\n") ? lines.Length : lines.Length - 1;

        for (int li = 0; li < complete; li++)
        {
            string raw = lines[li];
            string l = raw.Trim();
            if (l.Length == 0) continue;
            var m = Regex.Match(l, @"^cmd:m([0-7])_\d+:(.+)#$", RegexOptions.Singleline);
            if (m.Success)
            {
                // 填充字元必須整段都是同一個 sender 的字母,且長度分毫不差
                char expected = (char)('a' + int.Parse(m.Groups[1].Value));
                string pad    = m.Groups[2].Value;
                if (l.Length == PayloadBytes && pad.All(c => c == expected)) cmds++;
                else corrupt.Add($"長度 {l.Length}(應為 {PayloadBytes})開頭 '{l[..Math.Min(40, l.Length)]}'");
            }
            else if (Regex.IsMatch(l, @"^EDGELINK_PONG:[0-9a-f]{8}$")) pongs++;
            else corrupt.Add(l[..Math.Min(80, l.Length)]);
        }

        Assert.True(corrupt.Count == 0,
            $"有 {corrupt.Count} 行被切爛,例如 '{corrupt.FirstOrDefault()}'(完整 {cmds} 筆、PONG {pongs} 筆)");
        Assert.Equal(Senders * PerSender, cmds);
    }

    /// <summary>
    /// PING 必須回 PONG,且 token 要原樣帶回。
    ///
    /// 先前的併發測試只是把 PONG 數進一個變數、從頭到尾沒有斷言 —— 實測把回 PONG
    /// 那一行刪掉,12/12 照樣全過。而 SDK 文件寫明「連續 3 次沒回 PONG(約 15 秒)
    /// 就會被伺服器主動斷線」:現場每 15 秒被踢一次的迴歸,測試完全攔不住。
    /// </summary>
    [Fact]
    public async Task PING會被回以相同token的PONG()
    {
        int port = PortPool.Next();
        var listener = Listener(port);
        var messages = new List<string>();
        listener.OnMessage += m => { lock (messages) messages.Add(m); };
        listener.Start();

        var peer = Connect(port);
        Assert.True(await WaitUntil(() => listener.ConnectionCount == 1));

        var stream = peer.GetStream();
        var ping = Encoding.UTF8.GetBytes("EDGELINK_PING:deadbeef\n");
        await stream.WriteAsync(ping);

        peer.ReceiveTimeout = 3000;
        var buf = new byte[256];
        int n;
        try { n = peer.Client.Receive(buf); }
        catch (SocketException ex)
        {
            Assert.Fail($"送出 PING 之後 3 秒內沒有收到任何回應 —— 心跳沒有被回應 ({ex.SocketErrorCode})");
            return;
        }
        Assert.Equal("EDGELINK_PONG:deadbeef", Encoding.UTF8.GetString(buf, 0, n).Trim());

        // 協定訊息不該外流給消費端
        lock (messages) Assert.Empty(messages);
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
