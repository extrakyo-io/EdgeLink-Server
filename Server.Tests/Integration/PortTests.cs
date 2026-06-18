using System.Net;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

[Collection("Integration")]
public class PortTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _createdIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        // Clean up any ports created during this test class
        foreach (var id in _createdIds)
        {
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); }
            catch { /* best-effort */ }
        }
        _client.Dispose();
    }

    // ── List ports ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPorts_ReturnsEmptyListInitially()
    {
        var resp = await _client.GetAsync("/api/ports");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        var ports = doc.RootElement.GetProperty("ports");
        Assert.Equal(JsonValueKind.Array, ports.ValueKind);
    }

    // ── Add port ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddTcpServerPort_Returns201()
    {
        var resp = await AddPort("TCP SERVER", "19001", name: "TestServer19001");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        Assert.False(string.IsNullOrEmpty(id));
    }

    [Fact]
    public async Task AddTcpClientPort_Returns201()
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "TestClient19002",
            netProtocol  = "TCP CLIENT",
            localPort    = "--",
            targetIp     = "127.0.0.1",
            remotePort   = "19002",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        _createdIds.Add(doc.RootElement.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task AddUdpPort_Returns201()
    {
        var resp = await AddPort("UDP", "19003", name: "TestUdp19003");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        _createdIds.Add(doc.RootElement.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task AddPort_AppearsInList()
    {
        var addResp = await AddPort("TCP SERVER", "19004", name: "TestServer19004");
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        var listResp = await _client.GetAsync("/api/ports");
        using var listDoc = await listResp.ReadDocAsync();

        bool found = listDoc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .Any(p => p.GetProperty("id").GetString() == id);
        Assert.True(found);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddPort_MissingProtocolName_Returns400()
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            netProtocol = "TCP SERVER",
            localPort   = "19010",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AddPort_MissingNetProtocol_Returns400()
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "MissingProtocol",
            localPort    = "19011",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AddPort_DuplicateProtocolName_Returns409()
    {
        var r1 = await AddPort("TCP SERVER", "19012", name: "DupName");
        using var doc1 = await r1.ReadDocAsync();
        _createdIds.Add(doc1.RootElement.GetProperty("id").GetString()!);

        var r2 = await AddPort("UDP", "19013", name: "DupName");
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
    }

    [Fact]
    public async Task AddPort_DuplicateNetProtocolAndLocalPort_Returns409()
    {
        var r1 = await AddPort("TCP SERVER", "19014", name: "UniqueNameA");
        using var doc1 = await r1.ReadDocAsync();
        _createdIds.Add(doc1.RootElement.GetProperty("id").GetString()!);

        var r2 = await AddPort("TCP SERVER", "19014", name: "UniqueNameB");
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
    }

    // ── Delete port ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePort_RemovesFromList()
    {
        var addResp = await AddPort("TCP SERVER", "19015", name: "ToDelete19015");
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;

        var delResp = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
        {
            Content = JsonBody(new { id }),
        });
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var listResp = await _client.GetAsync("/api/ports");
        using var listDoc = await listResp.ReadDocAsync();
        bool found = listDoc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .Any(p => p.GetProperty("id").GetString() == id);
        Assert.False(found);
    }

    [Fact]
    public async Task DeletePort_NonExistent_Returns404()
    {
        var resp = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
        {
            Content = JsonBody(new { id = "nonexistent" }),
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Update port ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePort_ChangesProtocolName()
    {
        var addResp = await AddPort("TCP SERVER", "19016", name: "Original19016");
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        var putResp = await _client.PutJsonAsync($"/api/ports/{id}", new
        {
            protocolName = "Updated19016",
            netProtocol  = "TCP SERVER",
            localPort    = "19016",
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var listResp = await _client.GetAsync("/api/ports");
        using var listDoc = await listResp.ReadDocAsync();
        bool updated = listDoc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .Any(p => p.GetProperty("id").GetString() == id &&
                      p.GetProperty("protocolName").GetString() == "Updated19016");
        Assert.True(updated);
    }

    // ── Toggle enabled ────────────────────────────────────────────────────────

    [Fact]
    public async Task ToggleEnabled_DisablesPort()
    {
        var addResp = await AddPort("TCP SERVER", "19017", name: "Toggle19017");
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        // Disable
        var resp = await _client.PostJsonAsync($"/api/ports/{id}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var listResp = await _client.GetAsync("/api/ports");
        using var listDoc = await listResp.ReadDocAsync();
        bool disabled = listDoc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .Any(p => p.GetProperty("id").GetString() == id &&
                      !p.GetProperty("isEnabled").GetBoolean());
        Assert.True(disabled);
    }

    [Fact]
    public async Task ToggleEnabled_NonExistent_Returns404()
    {
        var resp = await _client.PostJsonAsync("/api/ports/nonexistent/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Get TCP clients ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetClients_ForTcpServerPort_ReturnsEmptyList()
    {
        var addResp = await AddPort("TCP SERVER", "19018", name: "Clients19018");
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        var resp = await _client.GetAsync($"/api/ports/{id}/clients");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("clients").ValueKind);
    }

    [Fact]
    public async Task GetClients_ForTcpClientPort_Returns400()
    {
        // /clients 只開放給能列舉「裝置」的 port (TCP Server / UDP)。
        // TCP Client 自己就是 client，沒有可列舉的 sub-client，應回 400。
        var addResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "TcpClients19019",
            netProtocol  = "TCP CLIENT",
            localPort    = "--",
            targetIp     = "127.0.0.1",
            remotePort   = "19019",
        });
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        var resp = await _client.GetAsync($"/api/ports/{id}/clients");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Change mask ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeMask_ToOriginalData_Succeeds()
    {
        var addResp = await AddPort("TCP SERVER", "19020", name: "Mask19020");
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _createdIds.Add(id);

        var resp = await _client.PostJsonAsync($"/api/ports/{id}/mask", new { maskType = "OriginalData" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> AddPort(string protocol, string localPort, string name) =>
        _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = protocol,
            localPort,
        });

    private static System.Net.Http.StringContent JsonBody(object obj) =>
        new(System.Text.Json.JsonSerializer.Serialize(obj),
            System.Text.Encoding.UTF8, "application/json");
}
