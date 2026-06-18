using System.Collections.Concurrent;
using System.Net;

namespace EdgeLink.NetworkServer.TCP;

public class TcpClientMetrics
{
    public readonly IPEndPoint EndPoint;
    public readonly DateTime ConnectedAt = DateTime.UtcNow;

    private long _messageCount;
    private long _totalBytes;
    private long _lastActivityTicks;
    private long _windowBytes;
    private long _windowStartTicks;
    private double _lastCompletedRate;
    private readonly object _rateLock = new();
    private long _lastRttMsBits = BitConverter.DoubleToInt64Bits(-1.0);
    private readonly ConcurrentDictionary<long, long> _pendingPings = new();

    public const string PingPrefix = "EDGELINK_PING:";
    public const string PongPrefix = "EDGELINK_PONG:";

    public TcpClientMetrics(IPEndPoint endPoint)
    {
        EndPoint = endPoint;
        long now = DateTime.UtcNow.Ticks;
        _lastActivityTicks = now;
        _windowStartTicks  = now;
    }

    public void RecordBytes(int count)
    {
        long now = DateTime.UtcNow.Ticks;
        Interlocked.Add(ref _totalBytes, count);
        Interlocked.Exchange(ref _lastActivityTicks, now);
        lock (_rateLock)
        {
            double elapsed = TimeSpan.FromTicks(now - _windowStartTicks).TotalSeconds;
            if (elapsed >= 5.0)
            {
                _lastCompletedRate = _windowBytes / elapsed;
                _windowBytes       = 0;
                _windowStartTicks  = now;
            }
            _windowBytes += count;
        }
    }

    public void RecordMessage() => Interlocked.Increment(ref _messageCount);

    public double GetRateBytesPerSec()
    {
        lock (_rateLock)
        {
            double elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _windowStartTicks).TotalSeconds;
            if (elapsed < 0.5) return _lastCompletedRate;
            return _windowBytes / Math.Max(elapsed, 0.1);
        }
    }

    public long   GetMessageCount()        => Interlocked.Read(ref _messageCount);
    public long   GetTotalBytes()          => Interlocked.Read(ref _totalBytes);
    public double GetConnectedSeconds()    => (DateTime.UtcNow - ConnectedAt).TotalSeconds;
    public double GetLastActivitySeconds() =>
        TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks)).TotalSeconds;
    public double GetLastRttMs()           =>
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastRttMsBits));

    public string BuildPingMessage()
    {
        long ticks = DateTime.UtcNow.Ticks;
        _pendingPings[ticks] = ticks;
        long cutoff = ticks - TimeSpan.FromSeconds(15).Ticks;
        foreach (var key in _pendingPings.Keys.Where(k => k < cutoff).ToList())
            _pendingPings.TryRemove(key, out _);
        return $"{PingPrefix}{ticks:X16}\n";
    }

    public bool TryHandlePong(string line)
    {
        if (!line.StartsWith(PongPrefix, StringComparison.Ordinal)) return false;
        if (long.TryParse(line[PongPrefix.Length..], System.Globalization.NumberStyles.HexNumber, null, out long ticks)
            && _pendingPings.TryRemove(ticks, out long sent))
        {
            double rtt = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - sent).TotalMilliseconds;
            Interlocked.Exchange(ref _lastRttMsBits, BitConverter.DoubleToInt64Bits(rtt));
        }
        return true;
    }

    public bool IsUnresponsive(int missedThreshold = 3) => _pendingPings.Count >= missedThreshold;
}
