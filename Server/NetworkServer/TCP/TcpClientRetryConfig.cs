namespace EdgeLink.NetworkServer.TCP;

public class TcpClientRetryConfig
{
    public int MaxRetryFirst       { get; set; } = -1;
    public int MaxRetrySubsequent  { get; set; } = -1;
    public int InitialDelayMs      { get; set; } = 1000;
    public int MaxDelayMs          { get; set; } = 30000;
    public int HeartbeatIntervalMs { get; set; } = 5000;
}
