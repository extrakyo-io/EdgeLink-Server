using System.Collections.Concurrent;
using EdgeLink.Infrastructure;

namespace EdgeLink.NetworkServer.Base;

public class AsyncMessageQueue<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0);
    private readonly int _maxSize;
    private volatile int _count;

    public AsyncMessageQueue(int maxSize = 10000) => _maxSize = maxSize;

    public void Enqueue(T item)
    {
        if (Interlocked.Increment(ref _count) > _maxSize)
        {
            Interlocked.Decrement(ref _count);
            AppLogger.Warning($"[AsyncMessageQueue] 已達最大容量 {_maxSize}，丟棄新訊息");
            return;
        }
        _queue.Enqueue(item);
        _semaphore.Release();
    }

    /// <summary>取出下一筆。只有在取消時才會結束(拋 OperationCanceledException)。
    ///
    /// <para>先前在 semaphore 與 queue 短暫不一致時會 <c>return default</c>,而所有消費端
    /// 都是 <c>if (data == null) break;</c> —— 於是一個純粹的偽喚醒就會把整條送出/路由
    /// 管線永久終結。不一致本身是 Clear() 與 Enqueue() 交錯造成的:Clear 先清 queue
    /// 再清 semaphore(兩個非原子步驟),若 Enqueue 的 Release() 剛好落在中間,就會留下
    /// 「semaphore 有許可但 queue 是空的」。</para>
    ///
    /// <para>改為繼續等下一筆。這裡刻意<b>不</b>把許可 Release 回去 —— 那個許可本來就沒有
    /// 對應的資料,消費掉它正好把計數還原成一致。</para></summary>
    public async Task<T?> DequeueAsync(CancellationToken ct = default)
    {
        while (true)
        {
            await _semaphore.WaitAsync(ct);
            if (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _count);
                return item;
            }
            AppLogger.Warning("[AsyncMessageQueue] semaphore/queue 計數不一致(偽喚醒)，已吸收並繼續等待");
        }
    }

    public int Count => _count;

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _count);
        while (_semaphore.CurrentCount > 0) _semaphore.Wait(0);
    }
}
