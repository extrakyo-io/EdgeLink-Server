using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Router;

namespace EdgeLink.NetworkServer.TCP;

public class TCPClientConnector : NetworkConnectorBase
{
    private readonly ConcurrentDictionary<string, TCPClientData> _tcpClientDatas = new();
    public Action<PortData>? OnReconnectSuccess;
    public Action<PortData>? OnReconnectFailed;

    private readonly IMainThreadDispatcher _dispatcher;
    private readonly TcpClientRetryConfig _config;

    public TCPClientConnector(IMainThreadDispatcher? dispatcher = null, TcpClientRetryConfig? retryConfig = null)
    {
        _dispatcher = dispatcher ?? DirectDispatcher.Instance;
        _config     = retryConfig ?? new TcpClientRetryConfig();
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
        if (!_tcpClientDatas.ContainsKey(portData.Key))
        {
            var clientData = new TCPClientData
            {
                portData = portData,
                tcpClient = new TcpClient(),
                CancellationTokenSource = new CancellationTokenSource()
            };
            _tcpClientDatas[portData.Key] = clientData;
            NetworkMessageRouter.Instance.RegisterTcpClient(portData.Key, clientData);
            _ = ConnectWithRetryAsync(clientData, isFirstConnect: true);
        }
    }

    public override void Connect(PortData portData)
    {
        if (_tcpClientDatas.TryGetValue(portData.Key, out var clientData))
        {
            ResetClientConnection(clientData);
            _ = ConnectWithRetryAsync(clientData, isFirstConnect: true);
        }
        else
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Not found", isError: true);
        }
    }

    public override Task Disconnect(PortData portData)
    {
        if (_tcpClientDatas.TryGetValue(portData.Key, out var clientData))
        {
            ResetClientConnection(clientData);
            portData.IsConnected = false;
        }
        return Task.CompletedTask;
    }

    public override Task RemovePort(PortData portData)
    {
        if (_tcpClientDatas.TryRemove(portData.Key, out var clientData))
        {
            ResetClientConnection(clientData);
            clientData.Dispose();
            portData.IsConnected = false;
            NetworkMessageRouter.Instance.UnregisterTcpClient(portData.Key);
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Removed");
        }
        return Task.CompletedTask;
    }

    public override async Task RestartPort(PortData portData)
    {
        if (_tcpClientDatas.TryGetValue(portData.Key, out var clientData))
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Restarting");
            await Disconnect(portData);

            // 不能 await:isFirstConnect 會採用 MaxRetryFirst(預設 -1),而 ShouldStopRetry 在
            // maxRetry < 0 時永遠回 false;ResetClientConnection 又會裝上全新未取消的 CTS,
            // 所以對離線裝置而言這是個無限迴圈 —— await 它會讓呼叫端(POST /api/ports/{id}/mask
            // 的 HTTP 請求)永遠不返回。改為 fire-and-forget,與 AddPort 的既有寫法一致。
            _ = ConnectWithRetryAsync(clientData, isFirstConnect: true).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    LogHelper.LogToConsole(
                        $"{LogHelper.Tag("TCP Client", portData)} Reconnect after restart failed: {t.Exception?.GetBaseException().Message}",
                        isError: true);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    public TCPClientData? GetClientData(PortData portData) =>
        _tcpClientDatas.TryGetValue(portData.Key, out var cd) ? cd : null;

    public override async Task ShutdownAsync()
    {
        LogHelper.LogToConsole($"[TCPClient] Shutting down {_tcpClientDatas.Count} TCP clients");
        foreach (var cd in _tcpClientDatas.Values)
        {
            try { cd.CancellationTokenSource?.Cancel(); }
            catch (Exception ex) { LogHelper.LogToConsole($"[TCPClient] Error cancelling: {ex.Message}"); }
        }
        await Task.Delay(100);
        foreach (var cd in _tcpClientDatas.Values)
        {
            try { ResetClientConnection(cd); cd.Dispose(); }
            catch (Exception ex) { LogHelper.LogToConsole($"[TCPClient] Error disposing: {ex.Message}"); }
        }
        _tcpClientDatas.Clear();
        LogHelper.LogToConsole("[TCPClient] All TCP clients closed");
    }

    private async Task ConnectWithRetryAsync(TCPClientData clientData, bool isFirstConnect)
    {
        var portData   = clientData.portData;
        var token      = clientData.CancellationTokenSource.Token;
        int retryCount = 0;
        int maxRetry   = isFirstConnect ? _config.MaxRetryFirst : _config.MaxRetrySubsequent;

        while (!ShouldStopRetry(clientData, retryCount, maxRetry))
        {
            try
            {
                if (!TryParsePort(portData.RemotePortDetails.Port, out int remotePort, "ConnectWithRetry"))
                {
                    portData.IsConnected = false;
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Invalid port", isError: true);
                    return;
                }

                clientData.tcpClient?.Close();
                clientData.tcpClient?.Dispose();
                clientData.tcpClient = new TcpClient { NoDelay = true };

                var connectTask = clientData.tcpClient.ConnectAsync(portData.TargetIP, remotePort);
                var timeoutTask = Task.Delay(5000, token);

                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    var timedOut = clientData.tcpClient;
                    clientData.tcpClient = null;
                    timedOut?.Close(); timedOut?.Dispose();
                    _ = connectTask.ContinueWith(t => { _ = t.Exception; });
                    throw new TimeoutException("TCP connect timeout");
                }
                if (connectTask.IsFaulted) _ = connectTask.Exception;

                if (clientData.tcpClient?.Connected == true)
                {
                    var stream = clientData.tcpClient.GetStream();
                    if (stream == null || !stream.CanWrite)
                        throw new Exception("Stream not writable");

                    ConfigureKeepAlive(clientData.tcpClient.Client);

                    var staleCts = clientData.CancellationTokenSource;
                    clientData.CancellationTokenSource = new CancellationTokenSource();
                    try { staleCts.Cancel();  } catch (ObjectDisposedException) { }
                    try { staleCts.Dispose(); } catch (ObjectDisposedException) { }

                    clientData.RequestQueue.Clear();

                    portData.IsConnected = true;
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Connected → {portData.TargetIP}:{portData.RemotePortDetails.Port}");
                    _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
                    OnReconnectSuccess?.Invoke(portData);

                    clientData.HeartbeatTask = StartHeartbeatAsync(clientData);

                    _ = StartReceiveAsync(clientData, stream).ContinueWith(t =>
                    {
                        if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} [Receive] {t.Exception?.Message}", isError: true);
                    });

                    bool isConcurrent = string.Equals(portData.RequestMode, "concurrent", StringComparison.OrdinalIgnoreCase);
                    bool isPolling    = string.Equals(portData.RequestMode, "polling",    StringComparison.OrdinalIgnoreCase);
                    if (!isConcurrent)
                    {
                        if (isPolling)
                            _ = ProcessPollingAsync(clientData, stream).ContinueWith(t =>
                            {
                                if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} [Polling] {t.Exception?.Message}", isError: true);
                            });
                        else
                            _ = ProcessSerialQueueAsync(clientData, stream).ContinueWith(t =>
                            {
                                if (t.IsFaulted) LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} [SerialQueue] {t.Exception?.Message}", isError: true);
                            });
                    }
                    return;
                }
                else
                {
                    throw new Exception("Connect failed");
                }
            }
            catch (TimeoutException)
            {
                portData.IsConnected = false;
                if (retryCount == 0)
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Connect timeout → {portData.TargetIP}:{portData.RemotePortDetails.Port}", isError: true);
                _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
            }
            catch (SocketException ex)
            {
                portData.IsConnected = false;
                if (retryCount == 0)
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Connect failed ({ex.SocketErrorCode}) → {portData.TargetIP}:{portData.RemotePortDetails.Port}", isError: true);
                _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
            }
            catch (Exception ex)
            {
                portData.IsConnected = false;
                if (retryCount == 0)
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Connect failed ({ex.Message}) → {portData.TargetIP}:{portData.RemotePortDetails.Port}", isError: true);
                _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
            }

            retryCount++;
            await Task.Delay(_config.InitialDelayMs, token);
        }

        LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Max retries reached ({maxRetry})", isError: true);
        OnReconnectFailed?.Invoke(portData);
    }

    private async Task StartHeartbeatAsync(TCPClientData clientData)
    {
        var portData = clientData.portData;
        var token    = clientData.CancellationTokenSource.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_config.HeartbeatIntervalMs, token);
                if (IsSocketDisconnected(clientData.tcpClient))
                {
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Heartbeat lost");
                    portData.IsConnected = false;
                    _dispatcher.Enqueue(() => portData.OnUpdate?.Invoke(portData));
                    _ = ConnectWithRetryAsync(clientData, isFirstConnect: false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Heartbeat error: {ex.Message}", isError: true);
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
            LogHelper.LogToConsole($"[TCPClient] ConfigureKeepAlive failed: {ex.Message}");
        }
    }

    private bool ShouldStopRetry(TCPClientData clientData, int retryCount, int maxRetry)
    {
        if (clientData.CancellationTokenSource.Token.IsCancellationRequested) return true;
        if (maxRetry < 0 || maxRetry == int.MaxValue) return false;
        return retryCount >= maxRetry;
    }

    private static bool IsSocketDisconnected(TcpClient? client)
    {
        try
        {
            if (client == null || !client.Connected) return true;
            var socket = client.Client;
            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch (ObjectDisposedException) { return true; }
        catch (SocketException ex)
        {
            LogHelper.LogToConsole($"[IsSocketDisconnected] SocketException: {ex.SocketErrorCode}");
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"[IsSocketDisconnected] Unexpected: {ex.Message}", isError: true);
            return true;
        }
    }

    private async Task StartReceiveAsync(TCPClientData clientData, NetworkStream stream)
    {
        var portData   = clientData.portData;
        var token      = clientData.CancellationTokenSource.Token;
        byte[] buffer  = new byte[2048];
        var lineBuffer = new StringBuilder();
        const int MaxBufferSize = 1024 * 1024;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead <= 0) break;

                string chunk;
                try { chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead); }
                catch { chunk = Encoding.GetEncoding("UTF-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetString(buffer, 0, bytesRead); }

                if (lineBuffer.Length + chunk.Length > MaxBufferSize) lineBuffer.Clear();
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
                    byte[] lineBytes = Encoding.UTF8.GetBytes(line);
                    await NetworkMessageRouter.Instance.RouteResponseAsync(clientData, lineBytes, line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            clientData.ResponseSignal?.TrySetCanceled();
            LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} Device connection lost (receive loop ended)");
        }
    }

    private async Task ProcessPollingAsync(TCPClientData clientData, NetworkStream stream)
    {
        var portData    = clientData.portData;
        var token       = clientData.CancellationTokenSource.Token;
        const int TimeoutMs = 3000;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await clientData.PollTrigger.WaitAsync(token);

                var slot = Interlocked.Exchange(ref clientData.LatestPollRequest, null);
                if (slot == null) continue;

                clientData.CurrentPendingRequester = slot.Requester;
                var signal = new TaskCompletionSource<bool>();
                clientData.ResponseSignal = signal;

                await clientData.DeviceWriteLock.WaitAsync(token);
                try
                {
                    await stream.WriteAsync(slot.Data, 0, slot.Data.Length, token);
                    RouterLogHelper.LogSend(portData, MonitorTargetType.TCPClient, Encoding.UTF8.GetString(slot.Data));
                }
                catch (Exception ex)
                {
                    clientData.CurrentPendingRequester = null;
                    clientData.ResponseSignal = null;
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} [Polling] Send failed: {ex.Message}", isError: true);
                    clientData.DeviceWriteLock.Release();
                    continue;
                }
                clientData.DeviceWriteLock.Release();

                await Task.WhenAny(signal.Task, Task.Delay(TimeoutMs, token));
                clientData.CurrentPendingRequester = null;
                clientData.ResponseSignal = null;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessSerialQueueAsync(TCPClientData clientData, NetworkStream stream)
    {
        var portData    = clientData.portData;
        var token       = clientData.CancellationTokenSource.Token;
        const int TimeoutMs = 5000;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var (requester, data) = await clientData.RequestQueue.DequeueAsync(token);
                if (data == null) break;

                clientData.CurrentPendingRequester = requester;
                var signal = new TaskCompletionSource<bool>();
                clientData.ResponseSignal = signal;

                await clientData.DeviceWriteLock.WaitAsync(token);
                try
                {
                    await stream.WriteAsync(data, 0, data.Length, token);
                    RouterLogHelper.LogSend(portData, MonitorTargetType.TCPClient, Encoding.UTF8.GetString(data));
                }
                catch (Exception ex)
                {
                    clientData.CurrentPendingRequester = null;
                    clientData.ResponseSignal = null;
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} [SerialQueue] Send failed: {ex.Message}", isError: true);
                    clientData.DeviceWriteLock.Release();
                    continue;
                }
                clientData.DeviceWriteLock.Release();

                var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeoutMs, token));
                if (completed == signal.Task && signal.Task.IsCanceled) break;

                if (completed != signal.Task)
                {
                    clientData.CurrentPendingRequester = null;
                    clientData.ResponseSignal = null;
                    LogHelper.LogToConsole($"{LogHelper.Tag("TCP Client", portData)} [SerialQueue] Response timeout ({TimeoutMs}ms)", isError: true);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ResetClientConnection(TCPClientData clientData)
    {
        try
        {
            if (clientData.CancellationTokenSource != null)
            {
                try { clientData.CancellationTokenSource.Cancel(); clientData.CancellationTokenSource.Dispose(); }
                catch (ObjectDisposedException) { }
            }
            try { clientData.tcpClient?.Close(); clientData.tcpClient?.Dispose(); }
            catch (Exception ex) { LogHelper.LogToConsole($"Error closing TcpClient: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            LogHelper.LogToConsole($"Error resetting TcpClient: {ex.Message}", isError: true);
        }
        finally
        {
            clientData.CancellationTokenSource = new CancellationTokenSource();
            clientData.tcpClient = new TcpClient();
        }
    }
}
