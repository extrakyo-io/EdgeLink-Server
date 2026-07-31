using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

/// <summary>
/// One end-to-end walkthrough of the whole system, run as a single CI smoke test:
/// login → define a mask → wire up a TCP Server/TCP Client port pair → push data
/// through the real routing pipeline in both directions → confirm Monitor, Logs
/// and Settings export all observe what happened → tear everything down.
///
/// This complements the narrower per-feature suites (AuthTests, MaskTests,
/// PortTests, RoutingTests, MonitorTests, SettingsTests, ...): those pin down
/// edge cases in isolation, this one only asks "does the whole path still work
/// together end to end".
/// </summary>
[Collection("Integration")]
public class SmokeTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds = [];
    private readonly List<string> _maskIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();

    public async Task DisposeAsync()
    {
        try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/monitor/port")); }
        catch { }

        foreach (var id in _portIds)
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); } catch { }

        foreach (var id in _maskIds)
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{id}")); }
            catch { }

        _client.Dispose();
    }

    [Fact]
    public async Task FullSystemWalkthrough_LoginToRoutingToMonitorToSettings()
    {
        // 1. Auth — anonymous caller is rejected; the fixture's authenticated client works.
        using (var anon = fixture.CreateClient())
        {
            var status = await anon.GetAsync("/api/auth/status");
            using var doc = await status.ReadDocAsync();
            Assert.False(doc.RootElement.GetProperty("authenticated").GetBoolean());
        }

        // 2. Mask — define a request-side transform (device kv-pairs -> JSON).
        string maskId = $"Smoke_{Guid.NewGuid().ToString("N")[..6]}";
        _maskIds.Add(maskId);
        await _client.PostJsonAsync("/api/masks", new { maskId });
        var maskPut = await _client.PutJsonAsync($"/api/masks/{maskId}", new
        {
            maskId,
            outputTemplate      = "{\"id\":\"{id}\",\"value\":{val}}",
            fieldDelimiter      = ";",
            kvSeparator         = ":",
            routeMode           = "",
            correlationIdField  = "",
            localizationKey     = "",
            description         = "",
            sampleData          = "",
        });
        Assert.Equal(HttpStatusCode.OK, maskPut.StatusCode);

        // 3. Ports — device-facing TCP Server wired to a remote-facing TCP Client.
        using var remote = new LocalTcpServer(19301);

        var srvResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "Smoke_Server",
            netProtocol  = "TCP SERVER",
            localPort    = "19350",
        });
        Assert.Equal(HttpStatusCode.Created, srvResp.StatusCode);
        using var srvDoc = await srvResp.ReadDocAsync();
        string serverId = srvDoc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(serverId);

        var cliResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName       = "Smoke_Client",
            netProtocol        = "TCP CLIENT",
            localPort          = "--",
            targetIp           = "127.0.0.1",
            remotePort         = "19301",
            maskType           = maskId,
            responseMaskType   = "OriginalData",
            requestMode        = "serial",
            sourceProtocolId   = serverId,
            sourceProtocolName = "",
        });
        Assert.Equal(HttpStatusCode.Created, cliResp.StatusCode);
        using var cliDoc = await cliResp.ReadDocAsync();
        _portIds.Add(cliDoc.RootElement.GetProperty("id").GetString()!);

        // 4. Monitor — point it at the device-facing port before traffic flows.
        var monResp = await _client.PostJsonAsync("/api/monitor/port", new { id = serverId });
        Assert.Equal(HttpStatusCode.OK, monResp.StatusCode);

        await Task.Delay(600); // let the TCP Client connect out to LocalTcpServer

        // 5. Routing — a real device connects, sends data, the mask transforms it,
        //    the "remote" side receives it, and a reply routes back unchanged.
        using var device = await ConnectDeviceAsync(19350);
        using var rConn   = await remote.AcceptAsync();

        await device.WriteLineAsync("id:DEV01;val:42");
        string? forwarded = await rConn.ReadDataLineAsync();
        Assert.NotNull(forwarded);
        Assert.Contains("\"id\":\"DEV01\"", forwarded);
        Assert.Contains("\"value\":42",    forwarded);

        await rConn.WriteLineAsync("ack:ok");
        string? reply = await device.ReadDataLineAsync();
        Assert.NotNull(reply);
        Assert.Contains("ack:ok", reply);

        // 6. Monitor/Logs — the traffic just generated should be observable.
        var monLogs = await _client.GetAsync("/api/monitor-logs");
        Assert.Equal(HttpStatusCode.OK, monLogs.StatusCode);
        using var monDoc = await monLogs.ReadDocAsync();
        Assert.Equal(JsonValueKind.Array, monDoc.RootElement.GetProperty("logs").ValueKind);

        var logs = await _client.GetAsync("/api/logs");
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);

        // 7. Settings — export should reflect the port/mask created in this run.
        var export = await _client.GetAsync("/api/settings/export");
        using var expDoc = await export.ReadDocAsync();
        bool hasPort = expDoc.RootElement.GetProperty("ports").EnumerateArray()
            .Any(p => p.GetProperty("protocolName").GetString() == "Smoke_Server");
        bool hasMask = expDoc.RootElement.GetProperty("masks").EnumerateArray()
            .Any(m => m.GetProperty("maskId").GetString() == maskId);
        Assert.True(hasPort, "exported settings should include the port created in this test");
        Assert.True(hasMask, "exported settings should include the mask created in this test");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    // (device PING/PONG handshake mirrors RoutingTests.ConnectDeviceAsync)

    private static async Task<TestTcpConnection> ConnectDeviceAsync(int edgeLinkServerPort)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", edgeLinkServerPort);
        var conn = new TestTcpConnection(tcp);
        string? ping = await conn.ReadRawLineAsync(timeout: 6000);
        if (ping != null && ping.StartsWith("EDGELINK_PING:"))
        {
            string hex = ping.Split(':')[1].Trim();
            await conn.WriteLineAsync($"EDGELINK_PONG:{hex}");
        }
        return conn;
    }

    private static System.Net.Http.StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
}
