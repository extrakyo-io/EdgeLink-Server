namespace EdgeLink.NetworkServer.Base.Models;

public class TcpClientInfo
{
    public string endpoint        = "";
    public string deviceId        = "";
    public float connectedSeconds;
    public float lastActivitySec;
    public long messageCount;
    public long totalBytes;
    public float rateBytesPerSec;
    public float rttMs;
}
