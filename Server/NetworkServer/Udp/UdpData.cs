using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.Udp;

// UDP is connectionless; we identify devices by the `id` field parsed from incoming
// messages, then mark them stale once they stop sending for longer than DeviceTimeout.
public class UdpDeviceState
{
    public string      DeviceId      = "";
    public IPEndPoint? Endpoint;
    public DateTime    FirstSeenUtc;
    public DateTime    LastSeenUtc;
    public long        MessageCount;
    public long        TotalBytes;
}

public class UdpData : DisposableBase
{
    public UdpClient? udpClient;
    public CancellationTokenSource CancellationTokenSource = new();
    public PortData portData = null!;
    public string SourceData = string.Empty;

    public readonly ConcurrentDictionary<string, UdpDeviceState> Devices = new();

    // Devices are marked stale after DeviceTimeout with no messages. Defaults to 30s;
    // set EDGELINK_UDP_DEVICE_TIMEOUT_SEC to override (integration tests use a short value).
    public TimeSpan DeviceTimeout = ResolveDeviceTimeout();

    private static TimeSpan ResolveDeviceTimeout()
    {
        var env = Environment.GetEnvironmentVariable("EDGELINK_UDP_DEVICE_TIMEOUT_SEC");
        if (double.TryParse(env, out double sec) && sec > 0)
            return TimeSpan.FromSeconds(sec);
        return TimeSpan.FromSeconds(30);
    }

    protected override void DisposeManagedResources()
    {
        if (CancellationTokenSource != null)
        {
            if (!CancellationTokenSource.IsCancellationRequested)
                CancellationTokenSource.Cancel();
            try { CancellationTokenSource.Dispose(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { AppLogger.Error($"[UdpData] Unexpected error disposing CTS: {ex.Message}"); }
            CancellationTokenSource = null!;
        }

        if (udpClient != null)
        {
            try { udpClient.Close(); } catch { }
            try { udpClient.Dispose(); } catch { }
            udpClient = null;
        }
    }
}
