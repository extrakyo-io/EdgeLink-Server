using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

/// <summary>
/// Tests that verify real TCP connections to EdgeLink TCP Server ports work correctly,
/// including EDGELINK_PING / PONG keepalive and message reception.
/// </summary>
[Collection("Integration")]
public class TcpNetworkTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        foreach (var id in _portIds)
        {
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); }
            catch { }
        }
        _client.Dispose();
    }

    // ── TCP connect / disconnect ───────────────────────────────────────────────

    [Fact]
    public async Task TcpServer_AcceptsConnection()
    {
        string id = await AddTcpServer("TcpAccept19050", 19050);

        using var tcp = await ConnectAndHandshakeAsync(19050);

        // Verify via API: at least 1 connected client
        var resp = await _client.GetAsync($"/api/ports/{id}/clients");
        using var doc = await resp.ReadDocAsync();
        int count = doc.RootElement.GetProperty("clients").GetArrayLength();
        Assert.True(count >= 1, $"Expected ≥1 connected client, got {count}");
    }

    [Fact]
    public async Task TcpServer_MultipleClients_AllAppearInClientList()
    {
        string id = await AddTcpServer("TcpMulti19051", 19051);

        using var t1 = await ConnectAndHandshakeAsync(19051);
        using var t2 = await ConnectAndHandshakeAsync(19051);
        using var t3 = await ConnectAndHandshakeAsync(19051);

        await Task.Delay(200);

        var resp = await _client.GetAsync($"/api/ports/{id}/clients");
        using var doc = await resp.ReadDocAsync();
        int count = doc.RootElement.GetProperty("clients").GetArrayLength();
        Assert.True(count >= 3, $"Expected ≥3 connected clients, got {count}");
    }

    [Fact]
    public async Task TcpServer_AfterDisconnect_ClientRemovedFromList()
    {
        string id = await AddTcpServer("TcpDisc19052", 19052);

        var tcp = await ConnectAndHandshakeAsync(19052);
        await Task.Delay(300);

        // Confirm connected
        var before = await _client.GetAsync($"/api/ports/{id}/clients");
        using var docBefore = await before.ReadDocAsync();
        Assert.True(docBefore.RootElement.GetProperty("clients").GetArrayLength() >= 1);

        // Disconnect
        tcp.Close();
        await Task.Delay(500);

        var after = await _client.GetAsync($"/api/ports/{id}/clients");
        using var docAfter = await after.ReadDocAsync();
        int count = docAfter.RootElement.GetProperty("clients").GetArrayLength();
        Assert.Equal(0, count);
    }

    // ── PING / PONG keepalive ─────────────────────────────────────────────────

    [Fact]
    public async Task TcpServer_SendsPingAfterConnect()
    {
        await AddTcpServer("TcpPing19053", 19053);

        using var tcp   = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", 19053);
        using var stream = tcp.GetStream();
        stream.ReadTimeout = 8000;   // pings come within ~3–5 s

        string? ping = await ReadLineAsync(stream, CancellationToken.None);

        Assert.NotNull(ping);
        Assert.StartsWith("EDGELINK_PING:", ping);
    }

    [Fact]
    public async Task TcpServer_KeepsConnectionAlive_AfterPongReply()
    {
        await AddTcpServer("TcpPong19054", 19054);

        using var tcp   = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", 19054);
        using var stream = tcp.GetStream();
        stream.ReadTimeout = 8000;

        // Read first PING and reply PONG
        string? ping = await ReadLineAsync(stream, CancellationToken.None);
        Assert.NotNull(ping);
        Assert.StartsWith("EDGELINK_PING:", ping);

        string hex  = ping!.Split(':')[1].Trim();
        string pong = $"EDGELINK_PONG:{hex}\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(pong));

        // A second PING should arrive (proves connection stays alive)
        stream.ReadTimeout = 12000;
        string? ping2 = await ReadLineAsync(stream, CancellationToken.None);
        Assert.NotNull(ping2);
        Assert.StartsWith("EDGELINK_PING:", ping2);
    }

    // ── Port enable / disable affects connectivity ─────────────────────────────

    [Fact]
    public async Task DisabledPort_RefusesConnections()
    {
        string id = await AddTcpServer("TcpDisabled19055", 19055);

        // Make one connection first to drain OS listen backlog
        using (var warmup = await ConnectAndHandshakeAsync(19055))
            warmup.Close();
        await Task.Delay(200);

        // Disable
        await _client.PostJsonAsync($"/api/ports/{id}/enabled", new { enabled = false });
        await Task.Delay(500);

        // Connection should now be refused
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            tcp.ConnectAsync("127.0.0.1", 19055)
               .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EnabledPort_ThenDisabled_StopsAccepting()
    {
        string id = await AddTcpServer("TcpToggle19056", 19056);

        // First connect succeeds
        using var t1 = await ConnectAndHandshakeAsync(19056);
        t1.Close();

        // Disable
        await _client.PostJsonAsync($"/api/ports/{id}/enabled", new { enabled = false });
        await Task.Delay(300);

        // Now connection should be refused
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            tcp.ConnectAsync("127.0.0.1", 19056)
               .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    // ── Send data to TCP Server ───────────────────────────────────────────────

    [Fact]
    public async Task TcpServer_ReceivesData_UpdatesTotalBytes()
    {
        string id = await AddTcpServer("TcpRecv19057", 19057);

        using var tcp   = await ConnectAndHandshakeAsync(19057);
        using var stream = tcp.GetStream();

        // Send some data
        byte[] data = Encoding.UTF8.GetBytes("hello:world\n");
        await stream.WriteAsync(data);
        await Task.Delay(300);

        // Check totalReceivedBytes via port list
        var listResp = await _client.GetAsync("/api/ports");
        using var doc = await listResp.ReadDocAsync();
        long received = doc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .Where(p => p.GetProperty("id").GetString() == id)
            .Select(p => p.GetProperty("totalReceivedBytes").GetInt64())
            .FirstOrDefault();

        Assert.True(received > 0, $"Expected totalReceivedBytes > 0, got {received}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> AddTcpServer(string name, int port, bool enabled = true)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "TCP SERVER",
            localPort    = port.ToString(),
            isEnabled    = enabled,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);

        if (enabled) await Task.Delay(200); // allow listener to start
        return id;
    }

    /// <summary>
    /// Connects a TcpClient and immediately reads + discards the first PING so the
    /// connection is in a "clean" state for the test.
    /// </summary>
    private static async Task<TcpClient> ConnectAndHandshakeAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var stream = tcp.GetStream();
        stream.ReadTimeout = 6000;

        // Read first PING
        string? ping = await ReadLineAsync(stream, CancellationToken.None);
        if (ping != null && ping.StartsWith("EDGELINK_PING:"))
        {
            string hex  = ping.Split(':')[1].Trim();
            string pong = $"EDGELINK_PONG:{hex}\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(pong));
        }
        return tcp;
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb  = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            int n;
            try { n = await stream.ReadAsync(buf, ct); }
            catch { return sb.Length > 0 ? sb.ToString() : null; }
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            char c = (char)buf[0];
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(c);
        }
    }

    private static System.Net.Http.StringContent JsonBody(object obj) =>
        new(System.Text.Json.JsonSerializer.Serialize(obj),
            System.Text.Encoding.UTF8, "application/json");
}
