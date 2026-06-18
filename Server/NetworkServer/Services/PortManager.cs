using System.Collections.Concurrent;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Connector;
using EdgeLink.NetworkServer.Logging;

namespace EdgeLink.NetworkServer.Services;

/// <summary>
/// Manages PortData lifecycle and bridges with NetworkConnectorCore.
/// Replaces Unity's NetworkPortManager MonoBehaviour.
/// </summary>
public class PortManager
{
    private static PortManager? _instance;
    public static PortManager Instance => _instance ?? throw new InvalidOperationException("PortManager not initialized");
    public static bool IsInitialized => _instance != null;

    public static void Initialize(NetworkConnectorCore core) => _instance = new PortManager(core);

    private readonly NetworkConnectorCore _core;
    private readonly PortDataStorageService _storage = new();
    private readonly object _lock = new();
    private readonly List<PortData> _ports = new();

    private PortManager(NetworkConnectorCore core)
    {
        _core = core;
    }

    public void LoadAndStart()
    {
        var loaded = _storage.LoadPortData();
        lock (_lock) _ports.AddRange(loaded);
        foreach (var p in loaded)
        {
            p.OnUpdate = OnPortUpdate;
            if (!p.IsEnabled) continue;
            try { _core.AddPort(p); }
            catch (Exception ex)
            {
                AppLogger.Warning($"[PortManager] Failed to start port '{p.ProtocolName}': {ex.Message}");
            }
        }
    }

    private void OnPortUpdate(PortData portData)
    {
        // In console app there's no UI to update — just log connected/disconnected changes
    }

    public List<PortData> GetAllPortDatas()
    {
        lock (_lock) return new List<PortData>(_ports);
    }

    public void AddPortData(PortData portData)
    {
        portData.Id  = Guid.NewGuid().ToString("N")[..8];
        portData.Key = $"{portData.NetProtocol}_{portData.Id}";
        portData.OnUpdate = OnPortUpdate;
        lock (_lock) _ports.Add(portData);
        SaveData();
        if (portData.IsEnabled)
            _core.AddPort(portData);
    }

    public async Task RemovePortData(PortData portData)
    {
        await _core.Stop(portData);
        lock (_lock) _ports.Remove(portData);
        SaveData();
    }

    public async Task UpdatePortData(PortData old, PortData updated)
    {
        await _core.Stop(old);

        old.ProtocolName      = updated.ProtocolName;
        old.NetProtocol       = updated.NetProtocol;
        old.LocalPortDetails  = updated.LocalPortDetails;
        old.RemotePortDetails = updated.RemotePortDetails;
        old.TargetIP          = updated.TargetIP;
        old.MaskType          = updated.MaskType;
        old.ResponseMaskType  = updated.ResponseMaskType;
        old.RequestMode       = updated.RequestMode;
        old.SourceProtocolName = updated.SourceProtocolName;
        old.SourceProtocolId  = updated.SourceProtocolId;

        SaveData();
        if (old.IsEnabled)
            _core.AddPort(old);
    }

    public async Task MaskSwitch(PortData portData)
    {
        await _core.RestartPort(portData);
    }

    public bool IsPortUnique(PortData portData)
    {
        lock (_lock)
        {
            return !_ports.Any(p =>
                p.ProtocolName == portData.ProtocolName ||
                (p.NetProtocol == portData.NetProtocol &&
                 p.LocalPortDetails?.Port == portData.LocalPortDetails?.Port &&
                 !string.IsNullOrEmpty(portData.LocalPortDetails?.Port) &&
                 portData.LocalPortDetails?.Port != "--"));
        }
    }

    public async Task TogglePortEnabled(string id, bool enabled)
    {
        PortData? port;
        lock (_lock) port = _ports.FirstOrDefault(p => p.Id == id);
        if (port == null) throw new KeyNotFoundException($"Port '{id}' not found");

        port.IsEnabled = enabled;
        SaveData();

        if (enabled)
            _core.AddPort(port);
        else
            await _core.Stop(port);
    }

    public void OnMonitorConsole(PortData portData)
    {
        _core.SetMonitorPort(portData);
    }

    public NetworkConnectorCore ConnectorCore => _core;

    public void SaveData()
    {
        List<PortData> snapshot;
        lock (_lock) snapshot = new List<PortData>(_ports);
        _storage.SavePortData(snapshot);
    }

    public async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        var shutdownTask = _core.UnInit();
        var completed = await Task.WhenAny(shutdownTask,
            Task.Delay(timeout ?? Timeout.InfiniteTimeSpan));
        if (completed != shutdownTask)
            AppLogger.Warning("[PortManager] Shutdown timed out — some connectors may still be running.");
    }
}
