using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.Base.Registry;

public enum NetProtocolType { TcpServer, TcpClient, Udp }

public class NetPortRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<NetProtocolType, Dictionary<string, PortData>> _reg = new()
    {
        { NetProtocolType.TcpServer, new() },
        { NetProtocolType.TcpClient, new() },
        { NetProtocolType.Udp,       new() },
    };

    public void Add(NetProtocolType type, string key, PortData data)
    {
        lock (_lock) _reg[type][key] = data;
    }

    public bool Remove(NetProtocolType type, string key)
    {
        lock (_lock) return _reg[type].Remove(key);
    }

    public PortData? Get(NetProtocolType type, string key)
    {
        lock (_lock) return _reg[type].TryGetValue(key, out var d) ? d : null;
    }

    public bool Contains(NetProtocolType type, string key)
    {
        lock (_lock) return _reg[type].ContainsKey(key);
    }

    public IEnumerable<PortData> GetAll()
    {
        lock (_lock) return _reg.Values.SelectMany(d => d.Values).ToList();
    }

    public void Clear()
    {
        lock (_lock) foreach (var d in _reg.Values) d.Clear();
    }
}
