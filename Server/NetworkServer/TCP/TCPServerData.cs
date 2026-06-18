using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.TCP;

public class TCPServerData : DisposableBase
{
    public TcpListener tcpListener = null!;
    public CancellationTokenSource CancellationTokenSource = new();
    public AsyncMessageQueue<(byte[] rawBytes, string text, IPEndPoint sourceEndpoint, string clientKey)> asyncMessageQueue = new();
    public IPEndPoint? RemoteEndPoint;
    public PortData portData = null!;
    public readonly ConcurrentDictionary<string, TcpClientMetrics> ConnectedClients = new();
    public readonly ConcurrentDictionary<string, NetworkStream> ClientStreams = new();
    public readonly ConcurrentDictionary<string, SemaphoreSlim> ClientWriteLocks = new();
    public readonly ConcurrentDictionary<string, string> ClientDeviceIds = new();

    private int totalConnections;
    private int currentConnections;
    private long totalReceivedBytes;
    private long totalSentBytes;
    private int recvCount;
    private int sendCount;

    public int TotalConnections    => totalConnections;
    public int CurrentConnections  => currentConnections;
    public long TotalReceivedBytes => totalReceivedBytes;
    public long TotalSentBytes     => totalSentBytes;
    public int RecvCount           => recvCount;
    public int SendCount           => sendCount;

    public string sourceData = string.Empty;

    public void IncrementTotalConnections()   => Interlocked.Increment(ref totalConnections);
    public void IncrementCurrentConnections() => Interlocked.Increment(ref currentConnections);

    public void DecrementCurrentConnections()
    {
        int cur, next;
        do
        {
            cur  = currentConnections;
            next = cur > 0 ? cur - 1 : 0;
        } while (Interlocked.CompareExchange(ref currentConnections, next, cur) != cur);
    }

    public void AddReceivedBytes(long bytes)  => Interlocked.Add(ref totalReceivedBytes, bytes);
    public void AddSentBytes(long bytes)      => Interlocked.Add(ref totalSentBytes, bytes);
    public void IncrementRecvCount()          => Interlocked.Increment(ref recvCount);
    public void IncrementSendCount()          => Interlocked.Increment(ref sendCount);

    protected override void DisposeManagedResources()
    {
        if (CancellationTokenSource != null)
        {
            if (!CancellationTokenSource.IsCancellationRequested)
                CancellationTokenSource.Cancel();
            CancellationTokenSource.Dispose();
            CancellationTokenSource = null!;
        }

        tcpListener?.Stop();
        tcpListener = null!;
    }
}
