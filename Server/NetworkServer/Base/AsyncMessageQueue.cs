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

    public async Task<T?> DequeueAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        if (_queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            return item;
        }
        _semaphore.Release();
        AppLogger.Error("[AsyncMessageQueue] semaphore/queue 計數不一致，已自動還原");
        return default;
    }

    public int Count => _count;

    public void Clear()
    {
        while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _count);
        while (_semaphore.CurrentCount > 0) _semaphore.Wait(0);
    }
}
