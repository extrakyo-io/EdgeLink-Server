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
        // 先前完全沒複製 Modbus,導致編輯 Modbus 埠時 registers/輪詢間隔/SlaveId 被靜默丟棄
        // (API 回 success,檔案卻寫回舊值)。用 ?? 而非直接指派:請求沒帶 modbus 區塊時
        // 保留原設定,避免把「只改名稱」的部分更新變成把 Modbus 設定清空。
        old.Modbus            = updated.Modbus ?? old.Modbus;

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

    /// <summary>序列化存檔。快照與寫檔必須在同一個鎖內完成 —— 先前是
    /// 「鎖內快照、鎖外寫檔」,兩個併發的儲存會出現:
    ///   • 丟失更新:A 快照(2 筆)→ B 快照(3 筆)並寫入 → A 寫入自己那份過期的 2 筆。
    ///     第 3 筆仍在記憶體(UI 看得到),重啟後才發現不見了。
    ///   • 靜默丟棄:兩者同時開檔 → 其中一個拋 sharing violation,被 catch 成一行 log,
    ///     但 API 仍回 200。
    /// 每個 HTTP 請求都跑在自己的 Task 上,所以這是真的會發生的。</summary>
    private readonly object _saveLock = new();

    public void SaveData()
    {
        lock (_saveLock)
        {
            List<PortData> snapshot;
            lock (_lock) snapshot = new List<PortData>(_ports);
            _storage.SavePortData(snapshot);
        }
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
