namespace EdgeLink.NetworkServer.Base;

public static class MonitorCounter
{
    private static int _counter;
    public static int Next()  => Interlocked.Increment(ref _counter);
    public static void Reset() => Interlocked.Exchange(ref _counter, 0);
}
