using System.Threading;
using System.Threading.Tasks;
using EdgeLink.NetworkServer.Base;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>#18(c):Clear() 與 Enqueue() 交錯會留下「semaphore 有許可但 queue 是空的」。
/// 先前 DequeueAsync 遇到這種偽喚醒會 return default,而所有消費端都是
/// `if (data == null) break;` —— 於是整條送出/路由管線被永久終結。</summary>
public class AsyncMessageQueueTests
{
    [Fact]
    public async Task Dequeue_ReturnsEnqueuedItem()
    {
        var q = new AsyncMessageQueue<string>();
        q.Enqueue("a");
        Assert.Equal("a", await q.DequeueAsync());
    }

    /// <summary>模擬偽喚醒:先 Enqueue 讓 semaphore 有許可,再把 queue 清空。
    /// 消費者不該因此結束,而應繼續等待下一筆真正的資料。</summary>
    [Fact]
    public async Task SpuriousWakeup_DoesNotEndConsumer()
    {
        var q = new AsyncMessageQueue<string>();

        q.Enqueue("will-be-cleared");
        q.Clear();          // queue 清空;若 semaphore 還留著許可就是偽喚醒
        q.Enqueue("real");  // 之後才進來的真資料

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var got = await q.DequeueAsync(cts.Token);

        Assert.Equal("real", got);   // 舊行為可能回 null,消費端就 break 了
    }

    /// <summary>沒有資料時應該持續等待,而不是回 default 讓消費端誤判為關閉。</summary>
    [Fact]
    public async Task EmptyQueue_BlocksUntilCancelled()
    {
        var q = new AsyncMessageQueue<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            async () => await q.DequeueAsync(cts.Token));
    }

    [Fact]
    public async Task Dequeue_PreservesFifoOrder()
    {
        var q = new AsyncMessageQueue<int>();
        for (int i = 0; i < 5; i++) q.Enqueue(i);
        for (int i = 0; i < 5; i++) Assert.Equal(i, await q.DequeueAsync());
    }
}
