using System.Net;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

[Collection("Integration")]
public class MonitorTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        // Clear monitor port
        try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/monitor/port")); }
        catch { }

        foreach (var id in _portIds)
        {
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); }
            catch { }
        }
        _client.Dispose();
    }

    // ── Get monitor port (initial state) ──────────────────────────────────────

    [Fact]
    public async Task GetMonitorPort_Initially_ReturnsEmpty()
    {
        // Clear first to ensure no leftover from other tests
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/monitor/port"));

        var resp = await _client.GetAsync("/api/monitor/port");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        string name = doc.RootElement.GetProperty("protocolName").GetString() ?? "";
        Assert.Equal("", name);
    }

    // ── Set monitor port ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetMonitorPort_ValidPort_Returns200()
    {
        var addResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "Monitor19040",
            netProtocol  = "TCP SERVER",
            localPort    = "19040",
        });
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);

        var resp = await _client.PostJsonAsync("/api/monitor/port", new { id });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task SetMonitorPort_ThenGet_ReturnsProtocolName()
    {
        var addResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "Monitor19041",
            netProtocol  = "TCP SERVER",
            localPort    = "19041",
        });
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);

        await _client.PostJsonAsync("/api/monitor/port", new { id });

        var getResp = await _client.GetAsync("/api/monitor/port");
        using var doc = await getResp.ReadDocAsync();
        Assert.Equal("Monitor19041", doc.RootElement.GetProperty("protocolName").GetString());
    }

    [Fact]
    public async Task SetMonitorPort_NonExistent_Returns404()
    {
        var resp = await _client.PostJsonAsync("/api/monitor/port", new { id = "notexist" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Clear monitor port ────────────────────────────────────────────────────

    [Fact]
    public async Task ClearMonitorPort_Returns200()
    {
        var resp = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/monitor/port"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ClearMonitorPort_ThenGet_ReturnsEmpty()
    {
        // Set then clear
        var addResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "Monitor19042",
            netProtocol  = "TCP SERVER",
            localPort    = "19042",
        });
        using var addDoc = await addResp.ReadDocAsync();
        string id = addDoc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);

        await _client.PostJsonAsync("/api/monitor/port", new { id });
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/monitor/port"));

        var getResp = await _client.GetAsync("/api/monitor/port");
        using var doc = await getResp.ReadDocAsync();
        Assert.Equal("", doc.RootElement.GetProperty("protocolName").GetString() ?? "");
    }

    // ── Console logs ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLogs_Returns200WithLogsArray()
    {
        var resp = await _client.GetAsync("/api/logs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        Assert.True(doc.RootElement.TryGetProperty("total", out _));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("logs").ValueKind);
    }

    [Fact]
    public async Task GetLogs_WithCursor_Returns200()
    {
        var resp = await _client.GetAsync("/api/logs?cursor=0");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Monitor logs ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMonitorLogs_Returns200WithLogsArray()
    {
        var resp = await _client.GetAsync("/api/monitor-logs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("logs").ValueKind);
    }

    // ── SSE stream ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MonitorStream_ReturnsEventStreamContentType()
    {
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = await fixture.CreateAuthenticatedClientAsync();

        try
        {
            using var resp = await client.GetAsync(
                "/api/monitor-stream",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string? ct = resp.Content.Headers.ContentType?.MediaType;
            Assert.Equal("text/event-stream", ct);
        }
        catch (OperationCanceledException) { /* expected on timeout */ }
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static System.Net.Http.StringContent JsonBody(object obj) =>
        new(System.Text.Json.JsonSerializer.Serialize(obj),
            System.Text.Encoding.UTF8, "application/json");
}
