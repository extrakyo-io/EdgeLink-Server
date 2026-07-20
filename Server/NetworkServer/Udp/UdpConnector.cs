using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeLink.Mask;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Router;

namespace EdgeLink.NetworkServer.Udp;

public class UdpConnector : NetworkConnectorBase
{
    private readonly ConcurrentDictionary<string, UdpData> _udpClients = new();
    private readonly IMainThreadDispatcher _dispatcher;

    public UdpConnector(IMainThreadDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? DirectDispatcher.Instance;
    }

    private bool TryParsePort(string? portString, out int port, string context = "")
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(portString))
        {
            LogHelper.LogToConsole($"[{context}] Port is empty", isError: true);
            return false;
        }
        if (!int.TryParse(portString, out port))
        {
            LogHelper.LogToConsole($"[{context}] Invalid port format: {portString}", isError: true);
            return false;
        }
        if (port < 1 || port > 65535)
        {
            LogHelper.LogToConsole($"[{context}] Port out of range: {port}", isError: true);
            return false;
        }
        return true;
    }

    private static bool TryParseIP(string? ipString, out IPAddress? ipAddress, string context = "")
    {
        ipAddress = null;
        if (string.IsNullOrWhiteSpace(ipString)) return true;
        if (!IPAddress.TryParse(ipString, out ipAddress))
        {
            LogHelper.LogToConsole($"[{context}] Invalid IP: {ipString}", isError: true);
            return false;
        }
        return true;
    }

    public override void AddPort(PortData portData)
    {
        if (_udpClients.TryGetValue(portData.Key, out var existing))
        {
            if (portData.IsConnected) { LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Already connected"); return; }
            portData.IsConnected = false;
            existing.Dispose();
            _udpClients.TryRemove(portData.Key, out _);
        }

        if (!TryParsePort(portData.RemotePortDetails.Port, out int remotePort, "AddPort"))
        {
            portData.IsConnected = false;
            return;
        }

        UdpClient udpClient;
        try { udpClient = new UdpClient(remotePort); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            portData.IsConnected = false;
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Port {portData.RemotePortDetails.Port} is already in use — skipping.", isError: true);
            return;
        }
        catch (Exception ex)
        {
            portData.IsConnected = false;
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Start failed: {ex.Message}", isError: true);
            return;
        }

        portData.IsConnected = true;
        var udpData = new UdpData
        {
            portData  = portData,
            udpClient = udpClient,
            CancellationTokenSource = new CancellationTokenSource()
        };
        _udpClients[portData.Key] = udpData;

        _ = ReceiveUdpMessages(udpData).ContinueWith(t =>
        {
            if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Receive error: {t.Exception}", isError: true);
        });
        _ = SweepStaleDevices(udpData).ContinueWith(t => { _ = t.Exception; });

        _dispatcher.Enqueue(() => SafeExecution.Safe(() => portData.OnUpdate?.Invoke(portData), "UdpConnector.OnUpdate"));
    }

    public override void Connect(PortData portData)
    {
        SafeExecution.Safe(() =>
        {
            if (!_udpClients.TryGetValue(portData.Key, out var udpData))
            {
                LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Not found", isError: true);
                return;
            }

            if (portData.IsConnected) { LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Already connected"); return; }
            if (!TryParsePort(portData.RemotePortDetails.Port, out int remotePort, "Connect")) return;

            try
            {
                udpData.udpClient?.Close(); udpData.udpClient?.Dispose();
                udpData.udpClient = new UdpClient(remotePort);

                udpData.CancellationTokenSource?.Cancel();
                udpData.CancellationTokenSource?.Dispose();
                udpData.CancellationTokenSource = new CancellationTokenSource();

                portData.IsConnected = true;
                _ = ReceiveUdpMessages(udpData).ContinueWith(t =>
                {
                    if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Receive error: {t.Exception}", isError: true);
                });
                _ = SweepStaleDevices(udpData).ContinueWith(t => { _ = t.Exception; });
                LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Reconnected → {portData.RemotePortDetails.Port}");
                _dispatcher.Enqueue(() => SafeExecution.Safe(() => portData.OnUpdate?.Invoke(portData), "UdpConnector.OnUpdate"));
            }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Reconnect failed: {ex.Message}", isError: true);
                portData.IsConnected = false;
            }
        }, "UdpConnector.Connect");
    }

    public override async Task Disconnect(PortData portData)
    {
        if (!_udpClients.TryGetValue(portData.Key, out var udpData))
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Not found");
            return;
        }
        try
        {
            udpData.CancellationTokenSource?.Cancel();
            await Task.Delay(300);
            udpData.udpClient?.Close(); udpData.udpClient?.Dispose();
            udpData.udpClient = null;
            udpData.CancellationTokenSource?.Dispose();
            udpData.CancellationTokenSource = new CancellationTokenSource();
            portData.IsConnected = false;
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Disconnected");
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Disconnect error: {ex}", isError: true);
        }
        _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
    }

    public override async Task RemovePort(PortData portData)
    {
        if (!_udpClients.TryRemove(portData.Key, out var udpData))
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Not found");
            return;
        }
        try
        {
            udpData.CancellationTokenSource?.Cancel();
            await Task.Delay(300);
            udpData.Dispose();
            portData.IsConnected = false;
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Removed");
            _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} Remove error: {ex}", isError: true);
        }
    }

    public override async Task RestartPort(PortData portData)
    {
        await Disconnect(portData);
        await Task.Delay(200);
        AddPort(portData);
    }

    public override async Task ShutdownAsync()
    {
        LogHelper.LogToConsole($"[UDP] Shutting down {_udpClients.Count} UDP connections");
        foreach (var ud in _udpClients.Values)
        {
            try { ud.CancellationTokenSource?.Cancel(); }
            catch (Exception ex) { LogHelper.LogToConsole($"[UDP] Error cancelling: {ex.Message}"); }
        }
        await Task.Delay(300);
        foreach (var ud in _udpClients.Values)
        {
            try { ud.Dispose(); }
            catch (Exception ex) { LogHelper.LogToConsole($"[UDP] Error disposing: {ex}"); }
        }
        _udpClients.Clear();
        LogHelper.LogToConsole("[UDP] All UDP connections closed");
    }

    private async Task ReceiveUdpMessages(UdpData udpData)
    {
        if (!TryParsePort(udpData.portData.LocalPortDetails.Port, out int localPort, "ReceiveUdpMessages"))
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", udpData.portData)} Invalid local port", isError: true);
            return;
        }
        if (!TryParseIP(udpData.portData.TargetIP, out IPAddress? targetIP, "ReceiveUdpMessages"))
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", udpData.portData)} Invalid target IP", isError: true);
            return;
        }

        using var sendClient = new UdpClient();
        IPEndPoint sendEndPoint = string.IsNullOrWhiteSpace(udpData.portData.TargetIP)
            ? new IPEndPoint(IPAddress.Broadcast, localPort)
            : new IPEndPoint(targetIP!, localPort);

        if (string.IsNullOrWhiteSpace(udpData.portData.TargetIP))
            sendClient.EnableBroadcast = true;

        byte[] buffer = new byte[1024];
        long lastUiUpdateTicks = 0;
        const long UiUpdateIntervalTicks = TimeSpan.TicksPerMillisecond * 100;
        var token = udpData.CancellationTokenSource.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (udpData.udpClient == null) break;

                var receiveTask  = udpData.udpClient.ReceiveAsync();
                var cancelTask   = Task.Delay(Timeout.Infinite, token);
                var completed    = await Task.WhenAny(receiveTask, cancelTask);
                if (completed == cancelTask) break;

                var result        = receiveTask.Result;
                int messageLength = result.Buffer.Length;
                udpData.portData.COMReceived += messageLength;

                string maskId = udpData.portData.MaskType?.Trim() ?? "OriginalData";
                var def = MaskDefinitionManager.Instance.GetDefinition(maskId)
                       ?? MaskDefinitionManager.Instance.GetDefinition("OriginalData");

                // ── 二進位 mask:直接吃原始 datagram bytes,不做 UTF8 解碼/切行 ──
                if (def?.binary != null)
                {
                    string outLine;
                    try
                    {
                        outLine = MaskProcessor.Process(def, result.Buffer, "");
                    }
                    catch (Exception ex)
                    {
                        // 這裡的 try 是包住整個 while 的,若讓例外逸出,一個設定錯誤
                        // (離譜的 offset、整數型別用了 "X2" 之類格式)就會讓這個埠
                        // 從此不再收任何 datagram 且不會自我恢復。只丟這一包。
                        LogHelper.LogToConsole(
                            $"{LogHelper.Tag("UDP", udpData.portData)} 二進位解碼失敗(已丟棄該封包,請檢查 Mask 設定): {ex.Message}",
                            isError: true);
                        continue;
                    }
                    if (!string.IsNullOrEmpty(outLine))
                    {
                        RouterLogHelper.LogReceive(udpData.portData, MonitorTargetType.UDP, outLine);
                        TrackDevice(udpData, def, outLine, messageLength, result.RemoteEndPoint);
                        var ob = Encoding.UTF8.GetBytes(outLine.EndsWith("\n") ? outLine : outLine + "\n");
                        _ = sendClient.SendAsync(ob, ob.Length, sendEndPoint).ContinueWith(t => { _ = t.Exception; });
                        udpData.portData.NetReceived += ob.Length;
                        RouterLogHelper.LogSend(udpData.portData, MonitorTargetType.UDP, outLine);
                        long nowB = DateTime.UtcNow.Ticks;
                        if (nowB - lastUiUpdateTicks >= UiUpdateIntervalTicks)
                        {
                            lastUiUpdateTicks = nowB;
                            _dispatcher.Enqueue(() => udpData.portData.OnUpdate?.Invoke(udpData.portData));
                        }
                    }
                    continue;
                }

                // ── 文字路徑 ──
                if (messageLength > buffer.Length) buffer = new byte[messageLength];
                Array.Copy(result.Buffer, buffer, messageLength);

                string message;
                try { message = Encoding.UTF8.GetString(buffer, 0, messageLength); }
                catch
                {
                    message = Encoding.GetEncoding("UTF-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetString(buffer, 0, messageLength);
                    LogHelper.LogToConsole($"{LogHelper.Tag("UDP", udpData.portData)} Invalid UTF-8 in data", isError: true);
                }

                udpData.SourceData = message;

                bool anyOutput = false;
                foreach (var rawLine in message.Split('\n'))
                {
                    string line = rawLine.Trim('\r', ' ');
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    RouterLogHelper.LogReceive(udpData.portData, MonitorTargetType.UDP, line);

                    byte[] lineBytes = Encoding.UTF8.GetBytes(line);
                    if (def != null) TrackDevice(udpData, def, line, lineBytes.Length, result.RemoteEndPoint);

                    string? output = def != null ? MaskProcessor.Process(def, lineBytes, line) : line;
                    if (string.IsNullOrEmpty(output)) continue;

                    var outBytes = Encoding.UTF8.GetBytes(output.EndsWith("\n") ? output : output + "\n");
                    _ = sendClient.SendAsync(outBytes, outBytes.Length, sendEndPoint)
                        .ContinueWith(t => { _ = t.Exception; });
                    udpData.portData.NetReceived += outBytes.Length;
                    RouterLogHelper.LogSend(udpData.portData, MonitorTargetType.UDP, output);
                    anyOutput = true;
                }

                if (anyOutput)
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (now - lastUiUpdateTicks >= UiUpdateIntervalTicks)
                    {
                        lastUiUpdateTicks = now;
                        _dispatcher.Enqueue(() => udpData.portData.OnUpdate?.Invoke(udpData.portData));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", udpData.portData)} Receive error: {ex.Message}", isError: true);
        }
    }

    private static void TrackDevice(UdpData udpData, MaskDefinition def, string line, int byteCount, IPEndPoint? sourceEndpoint)
    {
        var fields = NetworkMessageRouter.ExtractFields(def, line);
        string? devId = null;
        foreach (var kv in fields)
        {
            if (kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                devId = kv.Value;
                break;
            }
        }
        if (string.IsNullOrEmpty(devId)) return;

        var  now      = DateTime.UtcNow;
        bool wasAdded = false;
        udpData.Devices.AddOrUpdate(devId,
            _ =>
            {
                wasAdded = true;
                return new UdpDeviceState
                {
                    DeviceId     = devId,
                    Endpoint     = sourceEndpoint,
                    FirstSeenUtc = now,
                    LastSeenUtc  = now,
                    MessageCount = 1,
                    TotalBytes   = byteCount,
                };
            },
            (_, existing) =>
            {
                existing.Endpoint     = sourceEndpoint;
                existing.LastSeenUtc  = now;
                existing.MessageCount++;
                existing.TotalBytes  += byteCount;
                return existing;
            });

        if (wasAdded) EmitDeviceStatus(udpData, "CONNECTED", sourceEndpoint, devId);
    }

    private async Task SweepStaleDevices(UdpData udpData)
    {
        var token = udpData.CancellationTokenSource.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(5000, token); }
            catch (OperationCanceledException) { return; }

            var now = DateTime.UtcNow;
            int removed = 0;
            foreach (var kv in udpData.Devices)
            {
                if (now - kv.Value.LastSeenUtc > udpData.DeviceTimeout
                    && udpData.Devices.TryRemove(kv.Key, out var removedState))
                {
                    removed++;
                    EmitDeviceStatus(udpData, "DISCONNECTED", removedState.Endpoint, removedState.DeviceId);
                }
            }
            if (removed > 0)
                _dispatcher.Enqueue(() => SafeExecution.Safe(
                    () => udpData.portData.OnUpdate?.Invoke(udpData.portData),
                    "UdpConnector.SweepOnUpdate"));
        }
    }

    // Send EDGELINK_STATUS:CONNECTED/DISCONNECTED:protocol@deviceIp:deviceId\n
    // to the configured forward target (TargetIP:LocalPort). No-op if no forward target.
    private static void EmitDeviceStatus(UdpData udpData, string status, IPEndPoint? deviceEndpoint, string deviceId)
    {
        var portData = udpData.portData;
        if (string.IsNullOrEmpty(portData.TargetIP)) return;
        if (!int.TryParse(portData.LocalPortDetails?.Port, out int targetPort)) return;
        if (!IPAddress.TryParse(portData.TargetIP, out var targetIp)) return;

        string ipStr = deviceEndpoint?.Address?.ToString() ?? "";
        string body  = $"EDGELINK_STATUS:{status}:{portData.ProtocolName}@{ipStr}:{deviceId}\n";
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        try
        {
            using var client = new UdpClient();
            client.Send(bytes, bytes.Length, new IPEndPoint(targetIp, targetPort));
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("UDP", portData)} EmitDeviceStatus failed: {ex.Message}", isError: true);
        }
    }

    public List<TcpClientInfo> GetConnectedDevices(string portKey)
    {
        if (!_udpClients.TryGetValue(portKey, out var udpData)) return new List<TcpClientInfo>();
        var now = DateTime.UtcNow;
        return udpData.Devices.Values.Select(d =>
        {
            double connectedSec = Math.Max((now - d.FirstSeenUtc).TotalSeconds, 0.001);
            return new TcpClientInfo
            {
                endpoint         = d.Endpoint?.ToString() ?? "",
                deviceId         = d.DeviceId,
                connectedSeconds = (float)connectedSec,
                lastActivitySec  = (float)(now - d.LastSeenUtc).TotalSeconds,
                messageCount     = d.MessageCount,
                totalBytes       = d.TotalBytes,
                rateBytesPerSec  = (float)(d.TotalBytes / connectedSec),
                rttMs            = -1f,   // UDP has no RTT — WebUI displays N/A
            };
        }).ToList();
    }
}
