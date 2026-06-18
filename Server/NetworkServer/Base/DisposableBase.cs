namespace EdgeLink.NetworkServer.Base;

public abstract class DisposableBase : IDisposable
{
    private bool _disposed;
    private readonly object _lock = new();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeManagedResources();
        }
    }

    protected abstract void DisposeManagedResources();
}
