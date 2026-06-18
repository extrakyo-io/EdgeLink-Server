using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Modbus;
using EdgeLink.NetworkServer.TCP;
using EdgeLink.NetworkServer.Udp;

namespace EdgeLink.NetworkServer.Connector;

public class NetworkConnectorCore
{
    private readonly Dictionary<string, NetworkConnectorBase> _connectors;
    private readonly TCPServerConnector _tcpServerConnector;
    private readonly UdpConnector       _udpConnector;

    public NetworkConnectorCore(IMainThreadDispatcher? dispatcher = null)
    {
        _tcpServerConnector = new TCPServerConnector(dispatcher);
        _udpConnector       = new UdpConnector(dispatcher);
        _connectors = new Dictionary<string, NetworkConnectorBase>
        {
            { "UDP",                _udpConnector },
            { "TCP SERVER",         _tcpServerConnector },
            { "TCP CLIENT",         new TCPClientConnector(dispatcher) },
            { "MODBUS TCP MASTER",  new ModbusTcpMasterConnector() }
        };
    }

    public void Init(Action<string>? ssePublish = null)
    {
        LogHelper.Init(ssePublish);
    }

    public void AddPort(PortData portData)
    {
        if (!portData.IsEnabled)
        {
            portData.IsConnected = false;
            return;
        }

        string protocol = portData.NetProtocol.ToUpperInvariant();
        if (_connectors.TryGetValue(protocol, out var connector))
            connector.AddPort(portData);
        else
            LogHelper.LogToConsole($"Unknown protocol: {portData.NetProtocol}", isError: true);
    }

    public Task RestartPort(PortData portData)
    {
        if (_connectors.TryGetValue(portData.NetProtocol.ToUpperInvariant(), out var connector))
            return connector.RestartPort(portData);
        return Task.CompletedTask;
    }

    public void Connected(PortData portData)
    {
        if (_connectors.TryGetValue(portData.NetProtocol.ToUpperInvariant(), out var connector))
            connector.Connect(portData);
    }

    public Task Disconnected(PortData portData)
    {
        if (_connectors.TryGetValue(portData.NetProtocol.ToUpperInvariant(), out var connector))
            return connector.Disconnect(portData);
        return Task.CompletedTask;
    }

    public Task Stop(PortData portData)
    {
        if (_connectors.TryGetValue(portData.NetProtocol.ToUpperInvariant(), out var connector))
            return connector.RemovePort(portData);
        return Task.CompletedTask;
    }

    public void SetMonitorPort(PortData portData)
    {
        MonitorTargetType type = portData.NetProtocol.ToUpperInvariant() switch
        {
            "TCP SERVER" => MonitorTargetType.TCPServer,
            "TCP CLIENT" => MonitorTargetType.TCPClient,
            _            => MonitorTargetType.UDP
        };
        MonitorManager.Instance.SetMonitorPort(portData, type);
    }

    public List<TcpClientInfo> GetTcpServerClients(string portKey) =>
        _tcpServerConnector.GetConnectedClients(portKey);

    public List<TcpClientInfo> GetUdpDevices(string portKey) =>
        _udpConnector.GetConnectedDevices(portKey);

    public async Task UnInit()
    {
        await Task.WhenAll(_connectors.Values.Select(c => c.ShutdownAsync()));
    }
}
