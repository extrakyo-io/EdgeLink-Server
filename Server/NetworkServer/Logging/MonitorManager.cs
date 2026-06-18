using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.Logging;

public enum MonitorTargetType { TCPServer, TCPClient, UDP }

public class MonitorManager
{
    private static MonitorManager? _instance;
    public static MonitorManager Instance => _instance ??= new MonitorManager();

    private PortData? _port;
    private MonitorTargetType _type;

    private MonitorManager() { }

    public void SetMonitorPort(PortData portData, MonitorTargetType type)
    {
        _port = portData;
        _type = type;
    }

    public bool IsMonitoring(PortData incoming, MonitorTargetType type) =>
        _port != null && _type == type && ReferenceEquals(_port, incoming);

    public (PortData?, MonitorTargetType) GetMonitorInfo() => (_port, _type);
}
