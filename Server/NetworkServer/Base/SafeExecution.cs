using EdgeLink.Infrastructure;

namespace EdgeLink.NetworkServer.Base;

public static class SafeExecution
{
    public static void Safe(Action action, string context = "")
    {
        try { action?.Invoke(); }
        catch (Exception ex) { AppLogger.Error($"[Safe:{context}] {ex}"); }
    }

    public static async Task SafeAsync(Func<Task> asyncAction, string context = "")
    {
        try { if (asyncAction != null) await asyncAction(); }
        catch (Exception ex) { AppLogger.Error($"[SafeAsync:{context}] {ex}"); }
    }

    public static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        using (ct.Register(s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), tcs))
        {
            if (task != await Task.WhenAny(task, tcs.Task))
                throw new OperationCanceledException(ct);
        }
        return await task;
    }

    public static async Task WithCancellation(Task task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        using (ct.Register(s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), tcs))
        {
            if (task != await Task.WhenAny(task, tcs.Task))
                throw new OperationCanceledException(ct);
        }
        await task;
    }
}
