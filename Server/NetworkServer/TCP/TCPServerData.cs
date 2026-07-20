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

    /// <summary>
    /// 主動關閉所有「已接受」的 client 連線。
    ///
    /// 先前 Disconnect / RemovePort / ShutdownAsync / Dispose 都只取消 CTS 並停掉
    /// listener,完全沒碰 ClientStreams。問題是 CancellationToken **無法中止已經在進行中**
    /// 的 socket 讀取(.NET 只在操作開始前檢查 token),所以舊的接收迴圈會一直卡在
    /// ReadAsync,對端也收不到 FIN。
    ///
    /// 實際後果:改一次 mask 就會讓裝置的舊連線變成半開 —— 它下次送出的資料會被寫進
    /// 已無消費者的舊佇列(靜默遺失),而它自己完全不知道該重連。
    /// </summary>
    public void CloseAllClients()
    {
        foreach (var kv in ClientStreams)
        {
            try { kv.Value.Close(); }   catch { }
            try { kv.Value.Dispose(); } catch { }
        }
        ClientStreams.Clear();

        foreach (var kv in ClientWriteLocks)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        ClientWriteLocks.Clear();

        ConnectedClients.Clear();
        ClientDeviceIds.Clear();
    }

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

        // 取消 CTS 叫不醒卡在 ReadAsync 的接收迴圈,必須主動關閉 socket
        CloseAllClients();
    }
}
