using System.Net.Sockets;

namespace EdgeLink.NetworkServer.TCP;

public class PendingRequest
{
    public string ClientKey = "";
    public NetworkStream? Stream;
    public DateTime EnqueueTime;
}
