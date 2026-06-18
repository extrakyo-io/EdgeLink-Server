using System.Collections.Concurrent;
using System.Text;
using EdgeLink.Mask;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.TCP;

namespace EdgeLink.NetworkServer.Router;

public class NetworkMessageRouter
{
    private static NetworkMessageRouter? _instance;
    public static NetworkMessageRouter Instance => _instance ??= new NetworkMessageRouter();

    private readonly ConcurrentDictionary<string, TCPClientData> _tcpClients = new();
    private readonly ConcurrentDictionary<string, TCPServerData> _tcpServers = new();
    private const int ResponseTimeoutMs = 5000;

    private NetworkMessageRouter() { }

    public void RegisterTcpClient(string protocolKey, TCPClientData clientData)   => _tcpClients[protocolKey] = clientData;
    public void UnregisterTcpClient(string protocolKey)                           => _tcpClients.TryRemove(protocolKey, out _);
    public void RegisterTcpServer(string portId, TCPServerData serverData)        => _tcpServers[portId] = serverData;
    public void UnregisterTcpServer(string portId)                                => _tcpServers.TryRemove(portId, out _);

    public Task RouteMessageAsync(TCPServerData serverData, byte[] rawBytes, string parsedMessage,
        System.Net.IPEndPoint sourceEndpoint, string clientKey)
    {
        RouterLogHelper.LogReceive(serverData.portData, MonitorTargetType.TCPServer, parsedMessage, sourceEndpoint);
        TryIdentifyDevice(serverData, clientKey, parsedMessage);
        return RouteAndForwardAsync(serverData.portData, rawBytes, parsedMessage, clientKey, serverData);
    }

    // 給 Modbus poller / 其他內部產生器把已組好的訊息打進 forward pipeline。
    // 不走 TCPServerData (沒有 socket writeback)，只跑 mask + forward。
    public async Task InjectSynthesizedMessageAsync(PortData sourcePortData, string parsedMessage)
    {
        RouterLogHelper.LogReceive(sourcePortData, MonitorTargetType.UDP, parsedMessage, null);

        var targets = GetTargetClients(sourcePortData.Id, sourcePortData.ProtocolName);
        if (targets.Count == 0) return;

        byte[] rawBytes = Encoding.UTF8.GetBytes(parsedMessage);
        await Task.WhenAll(targets.Select(t => ForwardSynthesized(t, sourcePortData.ProtocolName, rawBytes, parsedMessage)));
    }

    private async Task ForwardSynthesized(TCPClientData client, string protocolName, byte[] rawBytes, string parsedMessage)
    {
        string maskId = client.portData?.MaskType?.Trim() ?? "OriginalData";
        var def = MaskDefinitionManager.Instance.GetDefinition(maskId)
               ?? MaskDefinitionManager.Instance.GetDefinition("OriginalData");
        if (def == null) { LogHelper.LogToConsole($"[Router] Mask not found: '{maskId}'", isError: true); return; }

        string? output = MaskProcessor.Process(def, rawBytes, parsedMessage);
        if (string.IsNullOrEmpty(output)) return;

        var bytes = Encoding.UTF8.GetBytes(output.EndsWith("\n") ? output : output + "\n");
        await TrySendToClient(client, protocolName, bytes);
    }

    // Extract deviceId from incoming message using source port's mask, regardless of forward target setup.
    // First identification triggers the deferred CONNECT notification with deviceId.
    private static void TryIdentifyDevice(TCPServerData serverData, string clientKey, string parsedMessage)
    {
        string maskId = serverData.portData?.MaskType?.Trim() ?? "OriginalData";
        var def = MaskDefinitionManager.Instance.GetDefinition(maskId)
               ?? MaskDefinitionManager.Instance.GetDefinition("OriginalData");
        if (def == null) return;

        var fields = ExtractFields(def, parsedMessage);
        // 韌體可能送 "id" 或 "ID"（甚至 "Id"），不分大小寫找第一個 match
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

        if (serverData.ClientDeviceIds.TryAdd(clientKey, devId))
        {
            var ep = serverData.ConnectedClients.TryGetValue(clientKey, out var m) ? m.EndPoint : null;
            TCPServerConnector.NotifyForwardTargetStatusChange("CONNECT", serverData.portData!, ep, devId);
        }
        else
        {
            serverData.ClientDeviceIds[clientKey] = devId;
        }
    }

    private async Task RouteAndForwardAsync(PortData portData, byte[] rawBytes, string parsedMessage,
        string clientKey, TCPServerData serverData)
    {
        try
        {
            var targets = GetTargetClients(portData.Id, portData.ProtocolName);
            if (targets.Count == 0) return;

            if (targets.Count == 1)
                await ProcessAndForward(targets[0], portData.ProtocolName, rawBytes, parsedMessage, clientKey, serverData);
            else
                await Task.WhenAll(targets.Select(t =>
                    ProcessAndForward(t, portData.ProtocolName, rawBytes, parsedMessage, clientKey, serverData)));
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"[Router] Unexpected error: {ex}", isError: true);
        }
    }

    private async Task ProcessAndForward(TCPClientData client, string protocolName,
        byte[] rawBytes, string parsedMessage, string clientKey, TCPServerData serverData)
    {
        string maskId = client.portData?.MaskType?.Trim() ?? "OriginalData";
        var def = MaskDefinitionManager.Instance.GetDefinition(maskId)
               ?? MaskDefinitionManager.Instance.GetDefinition("OriginalData");

        if (def == null)
        {
            LogHelper.LogToConsole($"[Router] Mask not found: '{maskId}'", isError: true);
            return;
        }

        bool isConcurrent = string.Equals(client.portData?.RequestMode, "concurrent", StringComparison.OrdinalIgnoreCase);
        bool isPolling    = string.Equals(client.portData?.RequestMode, "polling",    StringComparison.OrdinalIgnoreCase);

        if (isPolling)
        {
            string? output = MaskProcessor.Process(def, rawBytes, parsedMessage);
            if (string.IsNullOrEmpty(output)) return;

            var bytes  = Encoding.UTF8.GetBytes(output.EndsWith("\n") ? output : output + "\n");
            var stream = serverData.ClientStreams.GetValueOrDefault(clientKey);
            client.LatestPollRequest = new PollSlot
            {
                Requester = new PendingRequest { ClientKey = clientKey, Stream = stream, EnqueueTime = DateTime.UtcNow },
                Data = bytes
            };
            try { client.PollTrigger.Release(); } catch (SemaphoreFullException) { }
        }
        else if (isConcurrent)
        {
            string correlationId = Guid.NewGuid().ToString("N")[..8];
            var extra  = new Dictionary<string, string> { ["_corrId"] = correlationId };
            string? output = MaskProcessor.Process(def, rawBytes, parsedMessage, extra);
            if (string.IsNullOrEmpty(output)) return;

            var bytes  = Encoding.UTF8.GetBytes(output.EndsWith("\n") ? output : output + "\n");
            var stream = serverData.ClientStreams.GetValueOrDefault(clientKey);
            client.PendingRequests[correlationId] = new PendingRequest
            {
                ClientKey = clientKey, Stream = stream, EnqueueTime = DateTime.UtcNow
            };
            CleanupStalePendingRequests(client);
            await TrySendToClient(client, protocolName, bytes);
        }
        else
        {
            string? output = MaskProcessor.Process(def, rawBytes, parsedMessage);
            if (string.IsNullOrEmpty(output)) return;

            var bytes  = Encoding.UTF8.GetBytes(output.EndsWith("\n") ? output : output + "\n");
            var stream = serverData.ClientStreams.GetValueOrDefault(clientKey);
            client.RequestQueue.Enqueue((
                new PendingRequest { ClientKey = clientKey, Stream = stream, EnqueueTime = DateTime.UtcNow },
                bytes));
        }
    }

    public async Task RouteResponseAsync(TCPClientData clientData, byte[] rawBytes, string text)
    {
        var portData = clientData.portData;
        RouterLogHelper.LogReceive(portData, MonitorTargetType.TCPClient, text, null);

        if (!_tcpServers.TryGetValue(portData.SourceProtocolId ?? "", out var serverData))
            return;

        string maskId = portData.ResponseMaskType?.Trim() ?? "OriginalData";
        var def = MaskDefinitionManager.Instance.GetDefinition(maskId)
               ?? MaskDefinitionManager.Instance.GetDefinition("OriginalData");

        if (def == null) { LogHelper.LogToConsole($"[Router] Response mask not found: '{maskId}'", isError: true); return; }

        string? output = MaskProcessor.Process(def, rawBytes, text);
        if (string.IsNullOrEmpty(output)) return;

        var bytes     = Encoding.UTF8.GetBytes(output.EndsWith("\n") ? output : output + "\n");
        string routeMode = def.routeMode?.Trim().ToLower() ?? "broadcast";

        if (routeMode == "response")
        {
            bool isConcurrent = string.Equals(portData.RequestMode, "concurrent", StringComparison.OrdinalIgnoreCase);
            if (isConcurrent)
            {
                if (string.IsNullOrEmpty(def.correlationIdField)) return;
                var fields = ExtractFields(def, text);
                if (!fields.TryGetValue(def.correlationIdField, out var corrId)) return;
                if (!clientData.PendingRequests.TryRemove(corrId, out var req)) return;
                await TrySendToServerClient(serverData, req.ClientKey, req.Stream, bytes, portData.ProtocolName);
            }
            else
            {
                var requester = clientData.CurrentPendingRequester;
                if (requester == null) return;
                await TrySendToServerClient(serverData, requester.ClientKey, requester.Stream, bytes, portData.ProtocolName);
                clientData.CurrentPendingRequester = null;
                clientData.ResponseSignal?.TrySetResult(true);
            }
        }
        else
        {
            await BroadcastToServerClients(serverData, bytes, portData.ProtocolName);
        }
    }

    private async Task TrySendToServerClient(TCPServerData serverData, string clientKey,
        System.Net.Sockets.NetworkStream? stream, byte[] data, string protocolName)
    {
        if (stream == null) return;
        serverData.ClientWriteLocks.TryGetValue(clientKey ?? "", out var writeLock);
        try
        {
            using var cts = new CancellationTokenSource(2000);
            if (writeLock != null) await writeLock.WaitAsync(cts.Token);
            try
            {
                await stream.WriteAsync(data, 0, data.Length, cts.Token);
                RouterLogHelper.LogSend(serverData.portData, MonitorTargetType.TCPServer, Encoding.UTF8.GetString(data));
            }
            finally { writeLock?.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"[Router] Failed to write back to frontend client [{protocolName}]: {ex.Message}", isError: true);
        }
    }

    private Task BroadcastToServerClients(TCPServerData serverData, byte[] data, string protocolName)
    {
        var clients = serverData.ClientStreams.ToList();
        if (clients.Count == 0) return Task.CompletedTask;
        return Task.WhenAll(clients.Select(kv =>
            TrySendToServerClient(serverData, kv.Key, kv.Value, data, protocolName)));
    }

    public List<TCPClientData> GetTargetClients(string sourceId, string sourceName)
    {
        var targets = new List<TCPClientData>();
        foreach (var c in _tcpClients.Values.ToList())
        {
            if (c?.portData == null) continue;
            if (c.portData.SourceProtocolId == sourceId) targets.Add(c);
        }
        return targets;
    }

    private async Task TrySendToClient(TCPClientData tcpClient, string protocolName, byte[] data)
    {
        if (tcpClient?.portData == null) return;
        if (!tcpClient.portData.IsConnected) return;

        try
        {
            var client = tcpClient.tcpClient;
            if (client == null || !client.Connected) return;

            var stream = client.GetStream();
            if (stream == null || !stream.CanWrite)
            {
                tcpClient.portData.IsConnected = false;
                tcpClient.portData.OnUpdate?.Invoke(tcpClient.portData);
                return;
            }

            using var cts = new CancellationTokenSource(2000);
            await tcpClient.DeviceWriteLock.WaitAsync(cts.Token);
            try { await stream.WriteAsync(data, 0, data.Length, cts.Token); }
            finally { try { tcpClient.DeviceWriteLock.Release(); } catch (ObjectDisposedException) { } }

            RouterLogHelper.LogSend(tcpClient.portData, MonitorTargetType.TCPClient, Encoding.UTF8.GetString(data));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"[Router] Forward failed → {protocolName}: {ex.Message}", isError: true);
            try { tcpClient.tcpClient?.Close(); tcpClient.tcpClient?.Dispose(); } catch { }
            tcpClient.tcpClient = null;
            tcpClient.portData.IsConnected = false;
            tcpClient.portData.OnUpdate?.Invoke(tcpClient.portData);
        }
    }

    public static Dictionary<string, string> ExtractFields(MaskDefinition def, string text)
    {
        var result    = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;

        var fieldDelim = string.IsNullOrEmpty(def.fieldDelimiter) ? ";" : def.fieldDelimiter;
        var kvSep      = string.IsNullOrEmpty(def.kvSeparator)    ? ":" : def.kvSeparator;

        foreach (var field in text.Split(new[] { fieldDelim }, StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = field.IndexOf(kvSep, StringComparison.Ordinal);
            if (idx < 0) continue;
            var key = field[..idx].Trim();
            var val = field[(idx + kvSep.Length)..].Trim();
            if (!string.IsNullOrEmpty(key)) result[key] = val;
        }
        return result;
    }

    private static void CleanupStalePendingRequests(TCPClientData client)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-ResponseTimeoutMs);
        foreach (var kv in client.PendingRequests.ToList())
            if (kv.Value.EnqueueTime < cutoff)
                client.PendingRequests.TryRemove(kv.Key, out _);
    }

    public TCPClientData? GetTcpClient(string protocolKey)
    {
        _tcpClients.TryGetValue(protocolKey, out var cd);
        return cd;
    }
}
