using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeLink.Mask;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Router;

namespace EdgeLink.NetworkServer.TCP;

public class TCPServerConnector : NetworkConnectorBase
{
    private readonly ConcurrentDictionary<string, TCPServerData> _tcpServers = new();
    private const int MaxConnectionsPerServer = 100;
    private const int MaxBufferSize = 1024 * 1024;
    private readonly IMainThreadDispatcher _dispatcher;

    public TCPServerConnector(IMainThreadDispatcher? dispatcher = null)
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

    public override void AddPort(PortData portData)
    {
        if (_tcpServers.TryGetValue(portData.Key, out var existing))
        {
            if (portData.IsConnected)
            {
                LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Already connected");
                return;
            }
            portData.IsConnected = false;
            existing.Dispose();
            _tcpServers.TryRemove(portData.Key, out _);
        }

        if (!TryParsePort(portData.LocalPortDetails.Port, out int localPort, "AddPort"))
        {
            portData.IsConnected = false;
            return;
        }

        var listener = new TcpListener(IPAddress.Any, localPort);
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            portData.IsConnected = false;
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Port {portData.LocalPortDetails.Port} is already in use — skipping.", isError: true);
            return;
        }
        catch (Exception ex)
        {
            portData.IsConnected = false;
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Start failed: {ex.Message}", isError: true);
            return;
        }

        var serverData = new TCPServerData
        {
            portData = portData,
            tcpListener = listener,
            CancellationTokenSource = new CancellationTokenSource()
        };

        _tcpServers[portData.Key] = serverData;
        NetworkMessageRouter.Instance.RegisterTcpServer(portData.Id, serverData);

        _ = AcceptClientsAsync(serverData).ContinueWith(t =>
        {
            if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} AcceptClients error: {t.Exception}", isError: true);
        });
        _ = ProcessPacketsAsync(serverData).ContinueWith(t =>
        {
            if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} ProcessPackets error: {t.Exception}", isError: true);
        });

        _dispatcher.Enqueue(() => SafeExecution.Safe(() => portData.OnUpdate?.Invoke(portData), "TcpServerConnector.OnUpdate"));
    }

    public override async Task RemovePort(PortData portData)
    {
        if (!_tcpServers.TryGetValue(portData.Key, out var serverData)) return;
        try
        {
            serverData.CancellationTokenSource?.Cancel();
            serverData.tcpListener?.Stop();
            portData.IsConnected = false;

            await Task.Delay(300);
            serverData.Dispose();
            _tcpServers.TryRemove(portData.Key, out _);
            NetworkMessageRouter.Instance.UnregisterTcpServer(portData.Id);

            _dispatcher.Enqueue(() => SafeExecution.Safe(() => portData.OnUpdate?.Invoke(portData), "TcpServerConnector.RemovePort.OnUpdate"));
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Remove failed: {ex.Message}", isError: true);
        }
    }

    public override async Task RestartPort(PortData portData)
    {
        await Disconnect(portData);
        await Task.Delay(200);
        AddPort(portData);
    }

    public override void Connect(PortData portData)
    {
        if (!_tcpServers.TryGetValue(portData.Key, out var serverData))
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Not found", isError: true);
            return;
        }

        try
        {
            if (!TryParsePort(portData.LocalPortDetails.Port, out int localPort, "Connect"))
            {
                portData.IsConnected = false;
                return;
            }

            try { serverData.tcpListener?.Stop(); } catch { }
            serverData.tcpListener = new TcpListener(IPAddress.Any, localPort);
            serverData.tcpListener.Start();
            serverData.CancellationTokenSource?.Cancel();
            serverData.CancellationTokenSource?.Dispose();
            serverData.CancellationTokenSource = new CancellationTokenSource();
            serverData.asyncMessageQueue = new AsyncMessageQueue<(byte[], string, IPEndPoint, string)>();
            portData.IsConnected = false;

            _ = AcceptClientsAsync(serverData).ContinueWith(t =>
            {
                if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} AcceptClients error: {t.Exception}", isError: true);
            });
            _ = ProcessPacketsAsync(serverData).ContinueWith(t =>
            {
                if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} ProcessPackets error: {t.Exception}", isError: true);
            });

            _dispatcher.Enqueue(() => SafeExecution.Safe(() => portData.OnUpdate?.Invoke(portData), "TcpServerConnector.Connect.OnUpdate"));
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Restart failed: {ex.Message}", isError: true);
        }
    }

    public override async Task Disconnect(PortData portData)
    {
        if (!_tcpServers.TryGetValue(portData.Key, out var serverData)) return;
        try
        {
            serverData.CancellationTokenSource?.Cancel();
            serverData.tcpListener?.Stop();
            serverData.CancellationTokenSource?.Dispose();
            serverData.tcpListener = null!;
            serverData.CancellationTokenSource = null!;
            portData.IsConnected = false;

            await Task.Delay(300);
            _dispatcher.Enqueue(() => SafeExecution.Safe(() => portData.OnUpdate?.Invoke(portData), "TcpServerConnector.Disconnect.OnUpdate"));
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", portData)} Disconnect failed: {ex.Message}", isError: true);
        }
    }

    private async Task AcceptClientsAsync(TCPServerData serverData)
    {
        var token = serverData.CancellationTokenSource.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var acceptTask = serverData.tcpListener.AcceptTcpClientAsync();
                TcpClient client;
                try
                {
                    var cancelTask = Task.Delay(Timeout.Infinite, token);
                    var completed  = await Task.WhenAny(acceptTask, cancelTask);
                    if (completed == cancelTask)
                    {
                        _ = acceptTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        break;
                    }
                    client = acceptTask.Result;
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException)    { break; }
                catch (SocketException)            { break; }

                if (client != null)
                {
                    if (serverData.CurrentConnections >= MaxConnectionsPerServer)
                    {
                        LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} Max connections reached ({MaxConnectionsPerServer})", isError: true);
                        client.Close();
                        continue;
                    }

                    var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                    SafeExecution.Safe(() =>
                    {
                        serverData.RemoteEndPoint = remoteEndPoint;
                        serverData.portData.IsConnected = true;
                        serverData.IncrementTotalConnections();
                        serverData.IncrementCurrentConnections();
                        serverData.portData.CurrentConnections = serverData.CurrentConnections;
                        serverData.portData.TotalConnections   = serverData.TotalConnections;
                        LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} Connected: {remoteEndPoint} (waiting for device id)");
                        // CONNECT notification is deferred until first message identifies the device (see NetworkMessageRouter.ProcessAndForward).
                        _dispatcher.Enqueue(() => SafeExecution.Safe(() => serverData.portData.OnUpdate?.Invoke(serverData.portData)));
                    });

                    _ = ReceiveClientAsync(client, serverData).ContinueWith(t =>
                    {
                        if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} ReceiveClient error: {t.Exception}", isError: true);
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveClientAsync(TcpClient client, TCPServerData serverData)
    {
        var stream         = client.GetStream();
        var token          = serverData.CancellationTokenSource.Token;
        var sourceEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
        string clientKey   = Guid.NewGuid().ToString("N");
        var metrics        = new TcpClientMetrics(sourceEndpoint!);
        serverData.ConnectedClients[clientKey]   = metrics;
        serverData.ClientStreams[clientKey]       = stream;
        serverData.ClientWriteLocks[clientKey]   = new SemaphoreSlim(1, 1);

        ConfigureKeepAlive(client.Client);

        // 依此埠 Mask 決定收包模式:binary mask → 二進位分包解碼;否則 → 文字(換行分隔 + PING/PONG)。
        var  maskDef  = MaskDefinitionManager.Instance.GetDefinition(serverData.portData.MaskType?.Trim() ?? "OriginalData");
        bool isBinary = maskDef?.binary != null;

        // 只有文字埠送 app 層心跳。二進位埠不送(避免文字 PING 混進二進位串流),改靠 TCP keepalive 偵測斷線。
        if (!isBinary)
        {
            _ = SendPingsAsync(stream, metrics, token, serverData, clientKey).ContinueWith(t =>
            {
                if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} [Ping] {t.Exception?.Message}", isError: true);
            });
        }

        try
        {
            if (isBinary)
                await ReceiveBinaryLoopAsync(stream, serverData, sourceEndpoint, clientKey, metrics, maskDef!.binary!, token);
            else
                await ReceiveTextLoopAsync(stream, serverData, sourceEndpoint, clientKey, metrics, token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)    { }
        catch (IOException)                { }
        finally
        {
            client.Close();
            serverData.ConnectedClients.TryRemove(clientKey, out _);
            serverData.ClientStreams.TryRemove(clientKey, out _);
            if (serverData.ClientWriteLocks.TryRemove(clientKey, out var wl))
                try { wl.Dispose(); } catch (ObjectDisposedException) { }
            serverData.ClientDeviceIds.TryRemove(clientKey, out var disconnectedDeviceId);

            serverData.DecrementCurrentConnections();
            serverData.portData.IsConnected       = serverData.CurrentConnections > 0;
            serverData.portData.CurrentConnections = serverData.CurrentConnections;
            serverData.portData.TotalConnections   = serverData.TotalConnections;
            serverData.portData.TotalReceivedBytes = serverData.TotalReceivedBytes;

            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} Disconnected: {sourceEndpoint}");
            NotifyForwardTargetStatusChange("DISCONNECT", serverData.portData, sourceEndpoint, disconnectedDeviceId ?? "");
            _dispatcher.Enqueue(() => SafeExecution.Safe(() => serverData.portData.OnUpdate?.Invoke(serverData.portData)));
        }
    }

    // 文字收包:UTF-8 + 換行分行;消化 PONG,其餘進 routing 佇列。(原 ReceiveClientAsync 內邏輯)
    private async Task ReceiveTextLoopAsync(NetworkStream stream, TCPServerData serverData,
        IPEndPoint? sourceEndpoint, string clientKey, TcpClientMetrics metrics, CancellationToken token)
    {
        byte[] buffer  = new byte[2048];
        var lineBuffer = new StringBuilder();

        while (!token.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
            if (bytesRead <= 0) break;

            metrics.RecordBytes(bytesRead);
            serverData.AddReceivedBytes(bytesRead);
            serverData.portData.TotalReceivedBytes = serverData.TotalReceivedBytes;

            string chunk;
            try { chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead); }
            catch
            {
                chunk = Encoding.GetEncoding("UTF-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetString(buffer, 0, bytesRead);
                LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} Invalid UTF-8 sequence in data", isError: true);
            }

            if (lineBuffer.Length + chunk.Length > MaxBufferSize)
            {
                LogHelper.LogToConsole($"{LogHelper.Tag("TCP Server", serverData.portData)} Buffer overflow ({MaxBufferSize} bytes)", isError: true);
                lineBuffer.Clear();
                if (chunk.Length > MaxBufferSize) continue;
            }

            lineBuffer.Append(chunk);
            string current  = lineBuffer.ToString();
            int lastNewline = current.LastIndexOf('\n');
            if (lastNewline < 0) continue;

            string processable = current[..lastNewline];
            string remaining   = current[(lastNewline + 1)..];
            lineBuffer.Clear();
            lineBuffer.Append(remaining);

            foreach (var rawLine in processable.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (metrics.TryHandlePong(line)) continue;
                metrics.RecordMessage();
                serverData.asyncMessageQueue.Enqueue((Encoding.UTF8.GetBytes(line), line, sourceEndpoint!, clientKey));
            }
        }
    }

    // 二進位收包:BinaryStreamFramer 依 spec 分包,每包交 BinaryMaskDecoder 解成 KV 文字再進 routing 佇列。
    // 解碼後的 KV 交由下游輸出埠(建議 Mask=OriginalData 原樣轉發)送出;裝置 id 由 KV 的 id 欄位辨識。
    private async Task ReceiveBinaryLoopAsync(NetworkStream stream, TCPServerData serverData,
        IPEndPoint? sourceEndpoint, string clientKey, TcpClientMetrics metrics, BinarySpec spec, CancellationToken token)
    {
        var framer = new BinaryStreamFramer(spec);
        byte[] buffer = new byte[4096];

        while (!token.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
            if (bytesRead <= 0) break;

            metrics.RecordBytes(bytesRead);
            serverData.AddReceivedBytes(bytesRead);
            serverData.portData.TotalReceivedBytes = serverData.TotalReceivedBytes;

            framer.Append(buffer.AsSpan(0, bytesRead));
            byte[]? packet;
            while ((packet = framer.Next()) != null)
            {
                string? kv = BinaryMaskDecoder.Decode(packet, spec);
                if (string.IsNullOrEmpty(kv)) continue;   // 未知 variant / 長度不符 / template 缺欄位 → 丟棄
                metrics.RecordMessage();
                serverData.asyncMessageQueue.Enqueue((Encoding.UTF8.GetBytes(kv), kv, sourceEndpoint!, clientKey));
            }
        }
    }

    public static void NotifyForwardTargetStatusChange(string status, PortData sourcePortData, IPEndPoint? endpoint = null, string deviceId = "")
    {
        _ = NotifyAsync(status, sourcePortData, endpoint, deviceId)
            .ContinueWith(t => { _ = t.Exception; });
    }

    private static async Task NotifyAsync(string status, PortData sourcePortData, IPEndPoint? endpoint = null, string deviceId = "")
    {
        string edgeStatus    = status == "CONNECT" ? "CONNECTED" : "DISCONNECTED";
        string endpointStr   = endpoint?.Address?.ToString() ?? "";
        string deviceIdSuffix = string.IsNullOrEmpty(deviceId) ? "" : $":{deviceId}";
        string notifyMessage = $"EDGELINK_STATUS:{edgeStatus}:{sourcePortData.ProtocolName}@{endpointStr}{deviceIdSuffix}";
        byte[] notifyBytes   = Encoding.UTF8.GetBytes(notifyMessage + "\n");

        var targets = NetworkMessageRouter.Instance.GetTargetClients(sourcePortData.Id, sourcePortData.ProtocolName);
        foreach (var target in targets)
        {
            if (target?.tcpClient?.Connected != true) continue;
            using var cts = new CancellationTokenSource(1000);
            try
            {
                await target.DeviceWriteLock.WaitAsync(cts.Token);
                try
                {
                    if (target.tcpClient == null || !target.tcpClient.Connected) continue;
                    var stream = target.tcpClient.GetStream();
                    await stream.WriteAsync(notifyBytes, 0, notifyBytes.Length, cts.Token);
                    LogHelper.LogToMonitor($"[Router] Notify target [{target.portData?.ProtocolName}]: [{sourcePortData.ProtocolName}] {status}");
                }
                finally { try { target.DeviceWriteLock.Release(); } catch (ObjectDisposedException) { } }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogHelper.LogToConsole($"[Router] Notify failed [{sourcePortData.ProtocolName}] {status}: {ex.Message}", isError: true);
            }
        }
    }

    private async Task ProcessPacketsAsync(TCPServerData serverData)
    {
        var token = serverData.CancellationTokenSource.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (rawBytes, text, sourceEndpoint, clientKey) = await serverData.asyncMessageQueue.DequeueAsync(token);
                if (token.IsCancellationRequested || rawBytes == null) break;
                await NetworkMessageRouter.Instance.RouteMessageAsync(serverData, rawBytes, text, sourceEndpoint, clientKey);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendPingsAsync(NetworkStream stream, TcpClientMetrics metrics,
        CancellationToken token, TCPServerData serverData, string clientKey)
    {
        await Task.Delay(3000, token).ConfigureAwait(false);
        while (!token.IsCancellationRequested)
        {
            if (metrics.IsUnresponsive(missedThreshold: 3))
            {
                try { stream.Close(); } catch { }
                break;
            }
            try
            {
                string ping = metrics.BuildPingMessage();
                byte[] bytes = Encoding.UTF8.GetBytes(ping);
                serverData.ClientWriteLocks.TryGetValue(clientKey, out var writeLock);
                if (writeLock != null) await writeLock.WaitAsync(token);
                try { await stream.WriteAsync(bytes, 0, bytes.Length, token); }
                finally { writeLock?.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
            await Task.Delay(5000, token).ConfigureAwait(false);
        }
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            byte[] inValue = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(inValue, 0);
            BitConverter.GetBytes(10_000u).CopyTo(inValue, 4);
            BitConverter.GetBytes(1_000u).CopyTo(inValue, 8);
            socket.IOControl(IOControlCode.KeepAliveValues, inValue, null);
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"[TCPServer] ConfigureKeepAlive failed: {ex.Message}");
        }
    }

    public List<TcpClientInfo> GetConnectedClients(string portKey)
    {
        if (!_tcpServers.TryGetValue(portKey, out var s)) return new List<TcpClientInfo>();
        return s.ConnectedClients.Select(kv => new TcpClientInfo
        {
            endpoint         = kv.Value.EndPoint?.ToString() ?? "",
            deviceId         = s.ClientDeviceIds.TryGetValue(kv.Key, out var did) ? did : "",
            connectedSeconds = (float)kv.Value.GetConnectedSeconds(),
            lastActivitySec  = (float)kv.Value.GetLastActivitySeconds(),
            messageCount     = kv.Value.GetMessageCount(),
            totalBytes       = kv.Value.GetTotalBytes(),
            rateBytesPerSec  = (float)kv.Value.GetRateBytesPerSec(),
            rttMs            = (float)kv.Value.GetLastRttMs()
        }).ToList();
    }

    public TCPServerData? GetServerData(PortData portData) =>
        _tcpServers.TryGetValue(portData.Key, out var sd) ? sd : null;

    public override async Task ShutdownAsync()
    {
        LogHelper.LogToConsole($"[TCPServer] Shutting down {_tcpServers.Count} TCP servers");
        foreach (var server in _tcpServers.Values)
        {
            try { server.CancellationTokenSource?.Cancel(); server.tcpListener?.Stop(); }
            catch (Exception ex) { LogHelper.LogToConsole($"[TCPServer] Error stopping listener: {ex.Message}"); }
        }
        await Task.Delay(300);
        foreach (var server in _tcpServers.Values)
        {
            try { server.Dispose(); }
            catch (Exception ex) { LogHelper.LogToConsole($"[TCPServer] Error disposing: {ex.Message}"); }
        }
        _tcpServers.Clear();
        LogHelper.LogToConsole("[TCPServer] All TCP servers closed");
    }
}
