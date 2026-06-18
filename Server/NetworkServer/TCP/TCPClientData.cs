using System.Collections.Concurrent;
using System.Net.Sockets;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.TCP;

public class PollSlot
{
    public PendingRequest Requester = null!;
    public byte[] Data = null!;
}

public class TCPClientData : DisposableBase
{
    public TcpClient? tcpClient;
    public CancellationTokenSource CancellationTokenSource = new();
    public Task? HeartbeatTask;
    public PortData portData = null!;

    public readonly AsyncMessageQueue<(PendingRequest requester, byte[] data)> RequestQueue = new(64);
    public volatile PendingRequest? CurrentPendingRequester;
    public volatile TaskCompletionSource<bool>? ResponseSignal;

    public volatile PollSlot? LatestPollRequest;
    public readonly SemaphoreSlim PollTrigger = new(0, 1);

    public readonly ConcurrentDictionary<string, PendingRequest> PendingRequests = new();

    public readonly SemaphoreSlim DeviceWriteLock = new(1, 1);

    protected override void DisposeManagedResources()
    {
        if (CancellationTokenSource != null)
        {
            if (!CancellationTokenSource.IsCancellationRequested)
                CancellationTokenSource.Cancel();
            try { CancellationTokenSource.Dispose(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { AppLogger.Error($"[TCPClientData] Unexpected error disposing CTS: {ex.Message}"); }
            CancellationTokenSource = null!;
        }

        if (HeartbeatTask != null && !HeartbeatTask.IsCompleted)
        {
            try { HeartbeatTask.Wait(2000); }
            catch (AggregateException) { }
            catch (Exception) { }
            HeartbeatTask = null;
        }

        ResponseSignal?.TrySetCanceled();
        ResponseSignal = null;
        CurrentPendingRequester = null;
        LatestPollRequest = null;
        try { PollTrigger.Release(); } catch { }
        try { DeviceWriteLock.Dispose(); } catch { }

        foreach (var kv in PendingRequests)
            PendingRequests.TryRemove(kv.Key, out _);

        if (tcpClient != null)
        {
            try { tcpClient.Close(); } catch { }
            try { tcpClient.Dispose(); } catch { }
            tcpClient = null;
        }
    }
}
