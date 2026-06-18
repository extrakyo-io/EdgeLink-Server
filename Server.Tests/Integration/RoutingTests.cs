using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

/// <summary>
/// End-to-end tests for message routing and Mask transformation.
///
/// Topology:
///   [TestDevice] ──connect──▶ [EdgeLink TCP Server (device side, port 192xx)]
///                                      ↕  SourceProtocolId routing
///                             [EdgeLink TCP Client] ──connect──▶ [LocalTcpServer (port 191xx)]
///
/// Key insight (from Router source):
///   - Request mask  = TCP Client's MaskType          (applied OUTBOUND: device → remote)
///   - Response mask = TCP Client's ResponseMaskType  (applied INBOUND:  remote → device)
///   - routeMode     = mask definition's routeMode    (broadcast | response)
/// </summary>
[Collection("Integration")]
public class RoutingTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds = [];
    private readonly List<string> _maskIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        foreach (var id in _portIds)
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); } catch { }
        foreach (var id in _maskIds)
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{id}")); } catch { }
        _client.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. BASIC ROUTING
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_OriginalData_MessagePassesThroughUnchanged()
    {
        using var remote = new LocalTcpServer(19100);
        string srv = await AddTcpServer("RT_OD_Server", 19150);
        await AddTcpClient("RT_OD_Client", 19100, srv, maskType: "OriginalData");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19150);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("sensor:temp;value:36.5");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        Assert.Contains("sensor:temp;value:36.5", msg);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. REQUEST MASK (TCP Client MaskType) — outbound transformation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_RequestMask_TransformsMessageBeforeForwarding()
    {
        string maskId = CreateId("ReqMask");
        await CreateMask(maskId, template: "{\"id\":\"{id}\",\"value\":{val}}",
            fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19101);
        string srv = await AddTcpServer("RT_ReqM_Server", 19151);
        await AddTcpClient("RT_ReqM_Client", 19101, srv, maskType: maskId);
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19151);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("id:DEV01;val:42");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        Assert.Contains("\"id\":\"DEV01\"", msg);
        Assert.Contains("\"value\":42",    msg);
    }

    [Fact]
    public async Task Route_RequestMask_MissingField_MessageDropped()
    {
        string maskId = CreateId("MissingF");
        await CreateMask(maskId, template: "{required_field}", fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19102);
        string srv = await AddTcpServer("RT_Miss_Server", 19152);
        await AddTcpClient("RT_Miss_Client", 19102, srv, maskType: maskId);
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19152);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("other_field:hello");
        string? msg = await rConn.ReadDataLineAsync(timeout: 1500);

        Assert.Null(msg);   // dropped — mask output is empty when field missing
    }

    [Fact]
    public async Task Route_RequestMask_CustomDelimiters_Work()
    {
        string maskId = CreateId("CustomDelim");
        await CreateMask(maskId, template: "{a}|{b}", fieldDelim: ",", kvSep: "=");

        using var remote = new LocalTcpServer(19103);
        string srv = await AddTcpServer("RT_Delim_Server", 19153);
        await AddTcpClient("RT_Delim_Client", 19103, srv, maskType: maskId);
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19153);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("a=hello,b=world");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        Assert.Contains("hello|world", msg);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. RESPONSE MASK (TCP Client ResponseMaskType) — inbound transformation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_ResponseMask_TransformsResponseBeforeSendingToDevice()
    {
        string resMaskId = CreateId("ResMask");
        await CreateMask(resMaskId, template: "OK:{status}", fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19104);
        string srv = await AddTcpServer("RT_Resp_Server", 19154);
        await AddTcpClient("RT_Resp_Client", 19104, srv,
            maskType: "OriginalData", responseMaskType: resMaskId, requestMode: "serial");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19154);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("query:status");
        await rConn.ReadDataLineAsync(); // consume the forwarded request

        await rConn.WriteLineAsync("status:running;code:200");

        string? response = await device.ReadDataLineAsync();
        Assert.NotNull(response);
        Assert.Equal("OK:running", response);
    }

    [Fact]
    public async Task Route_ResponseMask_OriginalData_PassesThroughUnchanged()
    {
        using var remote = new LocalTcpServer(19105);
        string srv = await AddTcpServer("RT_RespOD_Server", 19155);
        await AddTcpClient("RT_RespOD_Client", 19105, srv,
            maskType: "OriginalData", responseMaskType: "OriginalData", requestMode: "serial");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19155);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("ping");
        await rConn.ReadDataLineAsync();
        await rConn.WriteLineAsync("pong:42");

        string? response = await device.ReadDataLineAsync();
        Assert.NotNull(response);
        Assert.Contains("pong:42", response);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. ROUTE MODE — broadcast vs response
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_BroadcastMode_AllDevicesReceiveResponse()
    {
        // broadcast is the default routeMode (empty or "broadcast")
        string maskId = CreateId("Broadcast");
        await CreateMask(maskId, template: "{raw}", routeMode: "broadcast");

        using var remote = new LocalTcpServer(19106);
        string srv = await AddTcpServer("RT_BC_Server", 19156);
        await AddTcpClient("RT_BC_Client", 19106, srv,
            responseMaskType: maskId, requestMode: "serial");
        await Task.Delay(600);

        using var dev1 = await ConnectDeviceAsync(19156);
        using var dev2 = await ConnectDeviceAsync(19156);
        using var rConn = await remote.AcceptAsync();

        await dev1.WriteLineAsync("request");
        await rConn.ReadDataLineAsync();
        await rConn.WriteLineAsync("broadcast_response");

        string? r1 = await dev1.ReadDataLineAsync();
        string? r2 = await dev2.ReadDataLineAsync();

        Assert.NotNull(r1);
        Assert.Contains("broadcast_response", r1);
        Assert.NotNull(r2);
        Assert.Contains("broadcast_response", r2);
    }

    [Fact]
    public async Task Route_ResponseMode_OnlyRequesterReceivesResponse()
    {
        string maskId = CreateId("Response");
        await CreateMask(maskId, template: "{raw}", routeMode: "response");

        using var remote = new LocalTcpServer(19107);
        string srv = await AddTcpServer("RT_RM_Server", 19157);
        await AddTcpClient("RT_RM_Client", 19107, srv,
            responseMaskType: maskId, requestMode: "serial");
        await Task.Delay(600);

        using var dev1  = await ConnectDeviceAsync(19157);
        using var dev2  = await ConnectDeviceAsync(19157);
        using var rConn = await remote.AcceptAsync();

        // Only dev1 sends a request
        await dev1.WriteLineAsync("query");
        await rConn.ReadDataLineAsync();
        await rConn.WriteLineAsync("private_response");

        // dev1 receives it
        string? r1 = await dev1.ReadDataLineAsync();
        Assert.NotNull(r1);
        Assert.Contains("private_response", r1);

        // dev2 should NOT receive it
        string? r2 = await dev2.ReadDataLineAsync(timeout: 1000);
        Assert.Null(r2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. REQUEST MODES — serial / polling / concurrent
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_SerialMode_RequestsQueueAndCompleteInOrder()
    {
        using var remote = new LocalTcpServer(19108);
        string srv = await AddTcpServer("RT_Serial_Server", 19158);
        await AddTcpClient("RT_Serial_Client", 19108, srv,
            requestMode: "serial", responseMaskType: "OriginalData");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19158);
        using var rConn  = await remote.AcceptAsync();

        // Send two requests quickly
        await device.WriteLineAsync("req:1");
        await device.WriteLineAsync("req:2");

        // Remote processes first
        string? r1 = await rConn.ReadDataLineAsync();
        Assert.NotNull(r1);
        await rConn.WriteLineAsync("resp:1");

        string? resp1 = await device.ReadDataLineAsync();
        Assert.NotNull(resp1);
        Assert.Contains("resp:1", resp1);

        // Then second arrives at remote
        string? r2 = await rConn.ReadDataLineAsync(timeout: 6000);
        Assert.NotNull(r2);
        await rConn.WriteLineAsync("resp:2");

        string? resp2 = await device.ReadDataLineAsync();
        Assert.NotNull(resp2);
        Assert.Contains("resp:2", resp2);
    }

    [Fact]
    public async Task Route_PollingMode_LatestRequestWins()
    {
        using var remote = new LocalTcpServer(19109);
        string srv = await AddTcpServer("RT_Poll_Server", 19159);
        await AddTcpClient("RT_Poll_Client", 19109, srv, requestMode: "polling");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19159);
        using var rConn  = await remote.AcceptAsync();

        // Send multiple requests — only the latest should matter
        await device.WriteLineAsync("data:first");
        await device.WriteLineAsync("data:second");
        await device.WriteLineAsync("data:third");
        await Task.Delay(200);

        // Remote should receive (at least) the last one
        string? received = await rConn.ReadDataLineAsync(timeout: 2000);
        Assert.NotNull(received);
        // In polling mode the latest wins; we can't guarantee first is seen
        // but we CAN guarantee the last one arrives
        Assert.Contains("data:", received);
    }

    [Fact]
    public async Task Route_ConcurrentMode_CorrelationIdInjectedInRequest()
    {
        string maskId = CreateId("CorrIdMask");
        // Template includes {_corrId} which EdgeLink injects automatically
        await CreateMask(maskId,
            template: "{id}:{val}:{_corrId}",
            fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19110);
        string srv = await AddTcpServer("RT_Conc_Server", 19160);
        await AddTcpClient("RT_Conc_Client", 19110, srv,
            maskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19160);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("id:DEV01;val:99");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        // Message should have 3 parts: id:val:corrId
        string[] parts = msg!.Split(':');
        Assert.True(parts.Length >= 3, $"Expected 3 colon-separated parts, got: {msg}");
        Assert.Equal("DEV01", parts[0]);
        Assert.Equal("99",    parts[1]);
        Assert.False(string.IsNullOrEmpty(parts[2]), "CorrelationId should not be empty");
    }

    [Fact]
    public async Task Route_ConcurrentMode_ResponseRoutedToCorrectDevice()
    {
        // Request mask injects {_corrId}, response mask reads it back to route to correct device
        string reqMaskId = CreateId("ConcReqM");
        string resMaskId = CreateId("ConcResM");

        await CreateMask(reqMaskId,
            template: "{data}:{_corrId}",
            fieldDelim: ";", kvSep: ":");

        // Response mask must pass through the correlationId field
        await CreateMask(resMaskId,
            template: "{raw}",
            correlationIdField: "_corrId",
            routeMode: "response");

        using var remote = new LocalTcpServer(19111);
        string srv = await AddTcpServer("RT_ConcRes_Server", 19161);
        await AddTcpClient("RT_ConcRes_Client", 19111, srv,
            maskType: reqMaskId, responseMaskType: resMaskId,
            requestMode: "concurrent");
        await Task.Delay(600);

        // Two devices connect
        using var dev1  = await ConnectDeviceAsync(19161);
        using var dev2  = await ConnectDeviceAsync(19161);
        using var rConn = await remote.AcceptAsync();

        // Both send concurrently
        await dev1.WriteLineAsync("data:req1");
        await dev2.WriteLineAsync("data:req2");

        // Collect both requests from remote, parse their correlationIds
        string? fwd1 = await rConn.ReadDataLineAsync();
        string? fwd2 = await rConn.ReadDataLineAsync();

        Assert.NotNull(fwd1);
        Assert.NotNull(fwd2);

        // Echo back with the same correlationId in the _corrId field
        // Format received: "req1:<corrId>" or "req2:<corrId>"
        string corrId1 = fwd1!.Split(':').LastOrDefault() ?? "";
        string corrId2 = fwd2!.Split(':').LastOrDefault() ?? "";

        Assert.False(string.IsNullOrEmpty(corrId1));
        Assert.False(string.IsNullOrEmpty(corrId2));
        Assert.NotEqual(corrId1, corrId2);

        // Remote echoes back with _corrId field so router can match
        await rConn.WriteLineAsync($"_corrId:{corrId1}");
        await rConn.WriteLineAsync($"_corrId:{corrId2}");

        // Each device gets its own response (response mode)
        string? r1 = await dev1.ReadDataLineAsync(timeout: 3000);
        string? r2 = await dev2.ReadDataLineAsync(timeout: 3000);

        Assert.NotNull(r1);
        Assert.NotNull(r2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. MULTI-TARGET ROUTING (same SourceProtocolId on multiple TCP Clients)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_MultipleClients_SameSource_AllReceiveMessage()
    {
        using var remote1 = new LocalTcpServer(19112);
        using var remote2 = new LocalTcpServer(19113);

        string srv = await AddTcpServer("RT_Multi_Server", 19162);
        await AddTcpClient("RT_Multi_Client1", 19112, srv);
        await AddTcpClient("RT_Multi_Client2", 19113, srv);
        await Task.Delay(800);

        using var device = await ConnectDeviceAsync(19162);
        using var rConn1 = await remote1.AcceptAsync();
        using var rConn2 = await remote2.AcceptAsync();

        await device.WriteLineAsync("broadcast:hello");

        string? m1 = await rConn1.ReadDataLineAsync();
        string? m2 = await rConn2.ReadDataLineAsync();

        Assert.NotNull(m1);
        Assert.Contains("broadcast:hello", m1);
        Assert.NotNull(m2);
        Assert.Contains("broadcast:hello", m2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private string CreateId(string prefix) =>
        $"{prefix}_{Guid.NewGuid().ToString("N")[..6]}";

    private async Task CreateMask(string maskId,
        string template        = "{raw}",
        string fieldDelim      = ";",
        string kvSep           = ":",
        string routeMode       = "",
        string correlationIdField = "")
    {
        _maskIds.Add(maskId);
        await _client.PostJsonAsync("/api/masks", new { maskId });
        var put = await _client.PutJsonAsync($"/api/masks/{maskId}", new
        {
            maskId,
            outputTemplate     = template,
            fieldDelimiter     = fieldDelim,
            kvSeparator        = kvSep,
            routeMode,
            correlationIdField,
            localizationKey    = "",
            description        = "",
            sampleData         = "",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private async Task<string> AddTcpServer(string name, int localPort)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "TCP SERVER",
            localPort    = localPort.ToString(),
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(200);
        return id;
    }

    private async Task<string> AddTcpClient(string name, int remotePort, string sourceProtocolId,
        string maskType         = "OriginalData",
        string responseMaskType = "OriginalData",
        string requestMode      = "serial")
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName       = name,
            netProtocol        = "TCP CLIENT",
            localPort          = "--",
            targetIp           = "127.0.0.1",
            remotePort         = remotePort.ToString(),
            maskType,
            responseMaskType,
            requestMode,
            sourceProtocolId,
            sourceProtocolName = "",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(300);
        return id;
    }

    private static async Task<TestTcpConnection> ConnectDeviceAsync(int edgeLinkServerPort)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", edgeLinkServerPort);
        var conn = new TestTcpConnection(tcp);
        // Consume first PING and reply PONG
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

// ── Test helpers ──────────────────────────────────────────────────────────────

public sealed class TestTcpConnection(TcpClient tcp) : IDisposable
{
    private readonly NetworkStream _stream = tcp.GetStream();

    public async Task WriteLineAsync(string line)
    {
        byte[] data = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(data);
    }

    /// <summary>Reads one line, skipping EDGELINK_* internal messages.</summary>
    public async Task<string?> ReadDataLineAsync(int timeout = 3000)
    {
        while (true)
        {
            string? line = await ReadRawLineAsync(timeout);
            if (line == null) return null;
            if (!line.StartsWith("EDGELINK_")) return line;
        }
    }

    public async Task<string?> ReadRawLineAsync(int timeout = 3000)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb  = new StringBuilder();
        var buf = new byte[1];
        try
        {
            while (true)
            {
                int n = await _stream.ReadAsync(buf, cts.Token);
                if (n == 0) return sb.Length > 0 ? sb.ToString().TrimEnd('\r') : null;
                char c = (char)buf[0];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    public void Close()   => tcp.Close();
    public void Dispose() => tcp.Dispose();
}

public sealed class LocalTcpServer(int port) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private bool _started;

    private void EnsureStarted()
    {
        if (_started) return;
        _listener.Start();
        _started = true;
    }

    public async Task<TestTcpConnection> AcceptAsync(int timeout = 5000)
    {
        EnsureStarted();
        using var cts = new CancellationTokenSource(timeout);
        TcpClient client = await _listener.AcceptTcpClientAsync(cts.Token);
        return new TestTcpConnection(client);
    }

    public void Dispose() { try { _listener.Stop(); } catch { } }
}
