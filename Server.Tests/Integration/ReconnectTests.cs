using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentModbus;
using Xunit;

namespace EdgeLink.Tests.Integration;

/// <summary>
/// Verifies EdgeLink's auto-reconnect / device-liveness machinery end-to-end:
///   • TCP Client re-dials its remote target after the established connection drops.
///   • TCP Server re-accepts a device that disconnects and reconnects.
///   • UDP device liveness: id-timeout emits DISCONNECTED, resume emits CONNECTED.
///   • Modbus TCP Master re-connects to the slave after it goes down and returns.
///
/// Timing note: the shared ServerFixture wipes Setting/ on start, so the TCP Client
/// retry config falls back to class defaults (HeartbeatIntervalMs = 5s, InitialDelayMs = 1s),
/// giving a re-dial within ~6-7s. The UDP test shortens DeviceTimeout via env var to keep CI fast.
/// </summary>
[Collection("Integration")]
public class ReconnectTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();

    public async Task DisposeAsync()
    {
        foreach (var id in _portIds)
        {
            try
            {
                await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                    { Content = JsonBody(new { id }) });
            }
            catch { }
        }
        _client.Dispose();
    }

    // ── 1. TCP Client — outbound auto-reconnect ────────────────────────────────

    [Fact]
    public async Task TcpClient_AutoReconnects_AfterRemoteConnectionDrops()
    {
        // EdgeLink TCP Client dials OUT to a local server we control.
        string src = await AddTcpServer("RC_Src", 19180);
        using var remote = new LocalTcpServer(19181);
        await AddTcpClient("RC_Dst", 19181, src);

        // 1) EdgeLink dials out — first connection is accepted.
        var conn1 = await AcceptOrNullAsync(remote, 8000);
        Assert.True(conn1 != null, "EdgeLink TCP Client never made the initial outbound connection");

        // 2) Drop the remote side. EdgeLink's heartbeat detects the loss and re-dials.
        conn1!.Close();
        conn1.Dispose();

        // 3) Auto-reconnect: a second outbound connection arrives.
        var conn2 = await AcceptOrNullAsync(remote, 15000);
        Assert.True(conn2 != null, "EdgeLink TCP Client did not auto-reconnect within 15s");
        conn2!.Dispose();
    }

    // ── 2. TCP Server — device reconnect ───────────────────────────────────────

    [Fact]
    public async Task TcpServer_DeviceReconnects_AfterDisconnect()
    {
        string id = await AddTcpServer("RC_SrvReconn", 19182);

        // Device connects.
        var t1 = await ConnectAndHandshakeAsync(19182);
        Assert.True(await WaitClientCountAsync(id, 1, 4000), "Device did not register on first connect");

        // Device drops.
        t1.Close();
        t1.Dispose();
        Assert.True(await WaitClientCountAsync(id, 0, 6000), "Server did not drop the disconnected device");

        // Device reconnects — server accepts it again.
        using var t2 = await ConnectAndHandshakeAsync(19182);
        Assert.True(await WaitClientCountAsync(id, 1, 6000), "Device did not re-register after reconnect");
    }

    // ── 3. UDP — device timeout then resume ────────────────────────────────────

    [Fact]
    public async Task Udp_DeviceTimeout_ThenResume_EmitsStatusTransitions()
    {
        // Shorten the liveness window so the sweep marks the device stale quickly.
        Environment.SetEnvironmentVariable("EDGELINK_UDP_DEVICE_TIMEOUT_SEC", "2");
        try
        {
            const int listenPort = 19190;   // EdgeLink LISTENS here          (RemotePort)
            const int fwdPort    = 19191;   // EdgeLink forwards data + status (TargetIP:LocalPort)

            using var receiver = new UdpClient(fwdPort);
            await AddUdp("RC_Udp", listenPort: listenPort, forwardPort: fwdPort);

            using var sender = new UdpClient();
            void SendData()
            {
                byte[] b = Encoding.UTF8.GetBytes("id:dev1;v:1\n");
                sender.Send(b, b.Length, "127.0.0.1", listenPort);
            }

            // First message → CONNECTED.
            SendData();
            Assert.True(await WaitForStatusAsync(receiver, "CONNECTED", "dev1", 6000),
                "Expected CONNECTED after the first UDP message");

            // Stop sending → sweep marks the device stale after DeviceTimeout → DISCONNECTED.
            Assert.True(await WaitForStatusAsync(receiver, "DISCONNECTED", "dev1", 12000),
                "Expected DISCONNECTED after the device stopped sending");

            // Resume → CONNECTED again.
            SendData();
            Assert.True(await WaitForStatusAsync(receiver, "CONNECTED", "dev1", 6000),
                "Expected CONNECTED after the device resumed sending");
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGELINK_UDP_DEVICE_TIMEOUT_SEC", null);
        }
    }

    // ── 4. Modbus TCP Master — reconnect after slave returns ───────────────────

    [Fact]
    public async Task Modbus_Master_Reconnects_AfterSlaveReturns()
    {
        const int mbPort = 19193;
        var server = StartModbusServer(mbPort);
        try
        {
            string id = await AddModbusMaster("RC_Modbus", mbPort);

            // 1) EdgeLink connects to the slave and starts polling.
            Assert.True(await WaitIsConnectedAsync(id, true, 8000),
                "Modbus Master never connected to the slave");

            // 2) Slave goes down → the poll read fails → EdgeLink marks it disconnected.
            server.Stop();
            server.Dispose();
            Assert.True(await WaitIsConnectedAsync(id, false, 8000),
                "Modbus Master did not detect the slave going down");

            // 3) Slave returns → the poll loop auto-reconnects on the next TryConnect.
            server = StartModbusServer(mbPort);
            Assert.True(await WaitIsConnectedAsync(id, true, 12000),
                "Modbus Master did not auto-reconnect after the slave returned");
        }
        finally
        {
            try { server.Stop(); } catch { }
            try { server.Dispose(); } catch { }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<string> AddTcpServer(string name, int port)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "TCP SERVER",
            localPort    = port.ToString(),
            isEnabled    = true,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(200);   // let the listener bind
        return id;
    }

    private async Task<string> AddTcpClient(string name, int remotePort, string sourceProtocolId)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName       = name,
            netProtocol        = "TCP CLIENT",
            localPort          = "--",
            targetIp           = "127.0.0.1",
            remotePort         = remotePort.ToString(),
            maskType           = "OriginalData",
            responseMaskType   = "OriginalData",
            requestMode        = "concurrent",
            sourceProtocolId,
            sourceProtocolName = "",
            isEnabled          = true,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(300);
        return id;
    }

    private async Task<string> AddUdp(string name, int listenPort, int forwardPort)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "UDP",
            localPort    = forwardPort.ToString(),   // forward + status target port
            remotePort   = listenPort.ToString(),    // EdgeLink listens here
            targetIp     = "127.0.0.1",
            maskType     = "OriginalData",
            isEnabled    = true,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(300);   // let the socket bind + sweep loop start
        return id;
    }

    private async Task<string> AddModbusMaster(string name, int port)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "Modbus TCP Master",
            localPort    = "",
            remotePort   = port.ToString(),
            targetIp     = "127.0.0.1",
            isEnabled    = true,
            modbus = new
            {
                slaveId          = 1,
                pollIntervalMs   = 200,
                connectTimeoutMs = 1000,
                readTimeoutMs    = 1000,
                deviceId         = "mbtest",
                registers = new[]
                {
                    new { name = "x", functionCode = 4, startAddress = 0, quantity = 1, dataType = "uint16" },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(300);
        return id;
    }

    /// <summary>Starts an in-process Modbus TCP slave (unit 1) on the given port, retrying the bind.</summary>
    private static ModbusTcpServer StartModbusServer(int port)
    {
        Exception? last = null;
        for (int i = 0; i < 20; i++)
        {
            var srv = new ModbusTcpServer();
            try
            {
                srv.AddUnit(1);
                srv.Start(new IPEndPoint(IPAddress.Loopback, port));
                return srv;
            }
            catch (Exception ex)
            {
                last = ex;
                try { srv.Dispose(); } catch { }
                Thread.Sleep(250);
            }
        }
        throw new InvalidOperationException($"Could not start ModbusTcpServer on {port}: {last?.Message}");
    }

    /// <summary>Polls the port list until the port's isConnected matches <paramref name="expected"/>.</summary>
    private async Task<bool> WaitIsConnectedAsync(string portId, bool expected, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _client.GetAsync("/api/ports");
            using var doc = await resp.ReadDocAsync();
            foreach (var p in doc.RootElement.GetProperty("ports").EnumerateArray())
            {
                if (p.GetProperty("id").GetString() == portId)
                {
                    if (p.GetProperty("isConnected").GetBoolean() == expected) return true;
                    break;
                }
            }
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>Polls the port's client list until it reports <paramref name="expected"/> clients.</summary>
    private async Task<bool> WaitClientCountAsync(string portId, int expected, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _client.GetAsync($"/api/ports/{portId}/clients");
            using var doc = await resp.ReadDocAsync();
            int count = doc.RootElement.GetProperty("clients").GetArrayLength();
            if (count == expected) return true;
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>Reads UDP datagrams until an EDGELINK_STATUS line matches, or times out.</summary>
    private static async Task<bool> WaitForStatusAsync(UdpClient receiver, string status, string deviceId, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var res = await receiver.ReceiveAsync(cts.Token);
                string msg = Encoding.UTF8.GetString(res.Buffer);
                foreach (var line in msg.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // e.g. EDGELINK_STATUS:CONNECTED:RC_Udp@127.0.0.1:dev1
                    if (line.StartsWith("EDGELINK_STATUS:", StringComparison.Ordinal) &&
                        line.Contains($":{status}:", StringComparison.Ordinal) &&
                        line.TrimEnd().EndsWith($":{deviceId}", StringComparison.Ordinal))
                        return true;
                }
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    private static async Task<TestTcpConnection?> AcceptOrNullAsync(LocalTcpServer server, int timeoutMs)
    {
        try { return await server.AcceptAsync(timeoutMs); }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Connects a TcpClient and answers the first EDGELINK_PING so the client is clean.</summary>
    private static async Task<TestTcpConnection> ConnectAndHandshakeAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var conn = new TestTcpConnection(tcp);

        string? ping = await conn.ReadRawLineAsync(6000);
        if (ping != null && ping.StartsWith("EDGELINK_PING:", StringComparison.Ordinal))
        {
            string hex = ping.Split(':')[1].Trim();
            await conn.WriteLineAsync($"EDGELINK_PONG:{hex}");
        }
        return conn;
    }

    private static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
}
