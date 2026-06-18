using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.Base;

public abstract class NetworkConnectorBase
{
    public abstract void      AddPort(PortData portData);
    public abstract Task      RemovePort(PortData portData);
    public abstract void      Connect(PortData portData);
    public abstract Task      Disconnect(PortData portData);
    public abstract Task      RestartPort(PortData portData);
    public abstract Task      ShutdownAsync();
}
