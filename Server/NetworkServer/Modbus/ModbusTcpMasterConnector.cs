using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Router;
using EdgeLink.NetworkServer.TCP;
using FluentModbus;

namespace EdgeLink.NetworkServer.Modbus;

// 內部追蹤資料：每個 Modbus port 自己一份
public class ModbusTcpMasterData
{
    public PortData? portData;
    public ModbusTcpClient? client;
    public CancellationTokenSource? cts;
    public Task? pollLoop;
    public DateTime LastSuccessUtc;
    public int ConsecutiveFailures;
}

// Modbus TCP Master：定時 polling 遠端 slave，把 register 結果組成
// id:<DeviceId>;<name>:<value>;... 訊息餵進 EdgeLink 路由
public class ModbusTcpMasterConnector : NetworkConnectorBase
{
    private readonly ConcurrentDictionary<string, ModbusTcpMasterData> _ports = new();

    public override void AddPort(PortData portData)
    {
        if (portData.Modbus == null)
        {
            LogHelper.LogToConsole($"{Tag(portData)} ModbusConfig 缺失，無法啟動", isError: true);
            return;
        }
        if (!_ports.ContainsKey(portData.Key))
        {
            var data = new ModbusTcpMasterData
            {
                portData = portData,
                cts = new CancellationTokenSource()
            };
            _ports[portData.Key] = data;
            data.pollLoop = Task.Run(() => PollLoopAsync(data, data.cts.Token));
        }
    }

    public override void Connect(PortData portData)
    {
        if (_ports.TryGetValue(portData.Key, out var data))
        {
            ResetConnection(data);
        }
    }

    public override Task Disconnect(PortData portData)
    {
        if (_ports.TryGetValue(portData.Key, out var data))
        {
            ResetConnection(data);
            portData.IsConnected = false;
            portData.OnUpdate?.Invoke(portData);
        }
        return Task.CompletedTask;
    }

    public override async Task RemovePort(PortData portData)
    {
        if (_ports.TryRemove(portData.Key, out var data))
        {
            data.cts?.Cancel();
            try { if (data.pollLoop != null) await data.pollLoop; } catch { }
            ResetConnection(data);
            data.cts?.Dispose();
        }
    }

    public override Task RestartPort(PortData portData)
    {
        if (_ports.TryGetValue(portData.Key, out var data))
        {
            ResetConnection(data);
        }
        return Task.CompletedTask;
    }

    public override async Task ShutdownAsync()
    {
        var snapshots = _ports.Values.ToList();
        _ports.Clear();
        foreach (var d in snapshots)
        {
            d.cts?.Cancel();
        }
        foreach (var d in snapshots)
        {
            try { if (d.pollLoop != null) await d.pollLoop; } catch { }
            ResetConnection(d);
            d.cts?.Dispose();
        }
    }

    private async Task PollLoopAsync(ModbusTcpMasterData data, CancellationToken token)
    {
        var pd = data.portData!;
        var cfg = pd.Modbus!;
        int pollMs = Math.Max(20, cfg.PollIntervalMs);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (data.client == null)
                {
                    await TryConnect(data, token);
                }

                if (data.client != null && data.client.IsConnected)
                {
                    await PollOnce(data, token);
                    await Task.Delay(pollMs, token);
                }
                else
                {
                    // 重連節流：失敗後等久一點，避免狂砸 connect
                    await Task.Delay(Math.Min(5000, 500 + data.ConsecutiveFailures * 500), token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"{Tag(pd)} Poll loop unexpected: {ex.Message}", isError: true);
                ResetConnection(data);
                await Task.Delay(1000, token).ContinueWith(_ => { });
            }
        }
    }

    private Task TryConnect(ModbusTcpMasterData data, CancellationToken token)
    {
        var pd = data.portData!;
        var cfg = pd.Modbus!;

        if (string.IsNullOrWhiteSpace(pd.TargetIP))
        {
            LogHelper.LogToConsole($"{Tag(pd)} TargetIP 缺失", isError: true);
            data.ConsecutiveFailures++;
            return Task.CompletedTask;
        }
        if (!IPAddress.TryParse(pd.TargetIP, out var ip))
        {
            LogHelper.LogToConsole($"{Tag(pd)} TargetIP 不合法: {pd.TargetIP}", isError: true);
            data.ConsecutiveFailures++;
            return Task.CompletedTask;
        }
        int port = 502;
        if (!string.IsNullOrWhiteSpace(pd.RemotePortDetails?.Port))
            int.TryParse(pd.RemotePortDetails.Port, out port);

        try
        {
            var c = new ModbusTcpClient
            {
                ReadTimeout = cfg.ReadTimeoutMs,
                WriteTimeout = cfg.ReadTimeoutMs,
                ConnectTimeout = cfg.ConnectTimeoutMs
            };
            c.Connect(new IPEndPoint(ip, port), ModbusEndianness.BigEndian);
            data.client = c;
            data.ConsecutiveFailures = 0;
            pd.IsConnected = true;
            pd.OnUpdate?.Invoke(pd);
            LogHelper.LogToConsole($"{Tag(pd)} 連線成功 → {ip}:{port}");
        }
        catch (Exception ex)
        {
            data.ConsecutiveFailures++;
            if (data.ConsecutiveFailures == 1 || data.ConsecutiveFailures % 10 == 0)
                LogHelper.LogToConsole($"{Tag(pd)} 連線失敗 ({data.ConsecutiveFailures}): {ex.Message}", isError: true);
            ResetConnection(data);
        }
        return Task.CompletedTask;
    }

    private async Task PollOnce(ModbusTcpMasterData data, CancellationToken token)
    {
        var pd = data.portData!;
        var cfg = pd.Modbus!;
        var client = data.client!;
        byte slaveId = (byte)Math.Clamp(cfg.SlaveId, 0, 255);

        var sb = new StringBuilder();
        string deviceId = string.IsNullOrWhiteSpace(cfg.DeviceId) ? pd.ProtocolName : cfg.DeviceId;
        sb.Append("id:").Append(deviceId);

        bool anyOk = false;

        foreach (var reg in cfg.Registers)
        {
            try
            {
                string valueStr = await ReadRegisterAsync(client, slaveId, reg, token);
                if (!string.IsNullOrEmpty(valueStr))
                {
                    sb.Append(';').Append(reg.Name).Append(':').Append(valueStr);
                    anyOk = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 個別 register 失敗不重連 — 可能只是這個地址不存在
                if (data.ConsecutiveFailures < 3)
                    LogHelper.LogToConsole($"{Tag(pd)} reg '{reg.Name}' read fail: {ex.Message}", isError: true);

                // 但連線層級的 exception 視為斷線
                if (ex is IOException || ex is SocketException || ex is ObjectDisposedException
                    || ex is InvalidOperationException)
                {
                    data.ConsecutiveFailures++;
                    pd.IsConnected = false;
                    pd.OnUpdate?.Invoke(pd);
                    ResetConnection(data);
                    return;
                }
            }
        }

        if (anyOk)
        {
            data.LastSuccessUtc = DateTime.UtcNow;
            string msg = sb.ToString();
            await NetworkMessageRouter.Instance.InjectSynthesizedMessageAsync(pd, msg);
        }
    }

    private static Task<string> ReadRegisterAsync(ModbusTcpClient client, byte slaveId, ModbusRegisterMap reg, CancellationToken token)
    {
        return Task.Run(() => ReadRegister(client, slaveId, reg), token);
    }

    private static string ReadRegister(ModbusTcpClient client, byte slaveId, ModbusRegisterMap reg)
    {
        int qty = Math.Max(1, reg.Quantity);

        // 32 位元型別需要 2 個暫存器。Quantity 預設為 1,使用者若沒特別填,
        // 讀回來只有 1 個暫存器 → RegistersToString 回空字串 → 該欄位被靜默丟棄。
        // 這裡依 DataType 自動補足,避免「設定看起來正確但值永遠不出現」。
        if (reg.FunctionCode == 3 || reg.FunctionCode == 4)
            qty = Math.Max(qty, RequiredRegisters(reg.DataType));

        switch (reg.FunctionCode)
        {
            case 1: // Read Coils
            {
                Span<byte> span = client.ReadCoils(slaveId, reg.StartAddress, qty);
                return BoolsToString(span, qty);
            }
            case 2: // Read Discrete Inputs
            {
                Span<byte> span = client.ReadDiscreteInputs(slaveId, reg.StartAddress, qty);
                return BoolsToString(span, qty);
            }
            case 3: // Read Holding Registers
            {
                Span<ushort> span = client.ReadHoldingRegisters<ushort>(slaveId, reg.StartAddress, qty);
                return RegistersToString(span, reg);
            }
            case 4: // Read Input Registers
            {
                Span<ushort> span = client.ReadInputRegisters<ushort>(slaveId, reg.StartAddress, qty);
                return RegistersToString(span, reg);
            }
            default:
                throw new InvalidOperationException($"FunctionCode {reg.FunctionCode} 不支援");
        }
    }

    private static string BoolsToString(ReadOnlySpan<byte> rawBits, int bitCount)
    {
        // FluentModbus 回傳的是 packed bytes，每個 byte 8 bits (LSB first)
        if (bitCount == 1)
        {
            bool b = (rawBits[0] & 0x01) != 0;
            return b ? "1" : "0";
        }
        // 多 bit 組成 unsigned integer
        int v = 0;
        for (int i = 0; i < bitCount; i++)
        {
            int byteIdx = i / 8;
            int bitIdx = i % 8;
            if (byteIdx >= rawBits.Length) break;
            if ((rawBits[byteIdx] & (1 << bitIdx)) != 0) v |= (1 << i);
        }
        return v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>該 DataType 至少需要幾個 16-bit 暫存器才能組出值。</summary>
    public static int RequiredRegisters(string? dataType) =>
        (dataType ?? "uint16").ToLowerInvariant() switch
        {
            "uint32" or "int32" or "float32" => 2,
            _ => 1,
        };

    private static string RegistersToString(ReadOnlySpan<ushort> regs, ModbusRegisterMap reg)
    {
        if (regs.Length == 0) return "";
        double raw;
        switch ((reg.DataType ?? "uint16").ToLowerInvariant())
        {
            case "int16":
                raw = (short)regs[0];
                break;
            case "uint32":
                if (regs.Length < 2) return "";
                raw = ((uint)regs[0] << 16) | regs[1];
                break;
            case "int32":
                if (regs.Length < 2) return "";
                raw = (int)(((uint)regs[0] << 16) | regs[1]);
                break;
            case "float32":
                if (regs.Length < 2) return "";
                uint bits = ((uint)regs[0] << 16) | regs[1];
                raw = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                break;
            default: // uint16
                raw = regs[0];
                break;
        }
        double scaled = raw * reg.Scale + reg.Offset;
        return scaled.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static void ResetConnection(ModbusTcpMasterData data)
    {
        try { data.client?.Disconnect(); } catch { }
        try { data.client?.Dispose(); } catch { }
        data.client = null;
    }

    private static string Tag(PortData pd) => $"[Modbus | {pd.ProtocolName}]";
}
