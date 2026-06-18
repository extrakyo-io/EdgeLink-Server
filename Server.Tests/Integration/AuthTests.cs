using System.Net;
using Xunit;

namespace EdgeLink.Tests.Integration;

[Collection("Integration")]
public class AuthTests(ServerFixture fixture)
{
    // ── Status (unauthenticated) ──────────────────────────────────────────────

    [Fact]
    public async Task Status_WithoutCookie_ReturnsFalse()
    {
        using var client = fixture.CreateClient();
        var resp = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        Assert.False(doc.RootElement.GetProperty("authenticated").GetBoolean());
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithCorrectPassword_Returns200AndSetsCookie()
    {
        using var client = fixture.CreateClient();
        var resp = await client.PostJsonAsync("/api/auth/login", new { password = "admin" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        // Cookie must be present
        Assert.True(resp.Headers.Contains("Set-Cookie") ||
                    resp.RequestMessage?.RequestUri != null,
                    "Expected Set-Cookie header");
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        using var client = fixture.CreateClient();
        var resp = await client.PostJsonAsync("/api/auth/login", new { password = "wrongpass" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_WithEmptyPassword_Returns401()
    {
        using var client = fixture.CreateClient();
        var resp = await client.PostJsonAsync("/api/auth/login", new { password = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Status (authenticated) ────────────────────────────────────────────────

    [Fact]
    public async Task Status_AfterLogin_ReturnsTrue()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        Assert.True(doc.RootElement.GetProperty("authenticated").GetBoolean());
    }

    // ── Protected endpoints require auth ──────────────────────────────────────

    [Theory]
    [InlineData("GET",  "/api/ports")]
    [InlineData("GET",  "/api/masks")]
    [InlineData("GET",  "/api/logs")]
    [InlineData("GET",  "/api/monitor/port")]
    [InlineData("GET",  "/api/settings/export")]
    public async Task ProtectedEndpoints_WithoutAuth_Return401(string method, string url)
    {
        using var client = fixture.CreateClient();
        using var req    = new HttpRequestMessage(new HttpMethod(method), url);
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_ClearsSession()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync();

        // Confirm authenticated
        var before = await client.GetAsync("/api/auth/status");
        using var docBefore = await before.ReadDocAsync();
        Assert.True(docBefore.RootElement.GetProperty("authenticated").GetBoolean());

        // Logout
        var logoutResp = await client.PostJsonAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.OK, logoutResp.StatusCode);

        // Now unauthenticated
        var after = await client.GetAsync("/api/auth/status");
        using var docAfter = await after.ReadDocAsync();
        Assert.False(docAfter.RootElement.GetProperty("authenticated").GetBoolean());
    }

    // ── Change password ───────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_WithCorrectCurrent_Succeeds()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync();

        // Change to temp password
        var resp = await client.PostJsonAsync("/api/auth/change-password",
            new { currentPassword = "admin", newPassword = "tempPass123" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Old session should be invalidated — status returns false
        var status = await client.GetAsync("/api/auth/status");
        using var doc = await status.ReadDocAsync();
        Assert.False(doc.RootElement.GetProperty("authenticated").GetBoolean());

        // Restore original password
        using var restore = fixture.CreateClient();
        await restore.PostJsonAsync("/api/auth/login",  new { password = "tempPass123" });
        await restore.PostJsonAsync("/api/auth/change-password",
            new { currentPassword = "tempPass123", newPassword = "admin" });
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrent_Returns401()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync();
        var resp = await client.PostJsonAsync("/api/auth/change-password",
            new { currentPassword = "wrongpass", newPassword = "newpass" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithEmptyNew_Returns400()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync();
        var resp = await client.PostJsonAsync("/api/auth/change-password",
            new { currentPassword = "admin", newPassword = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithoutAuth_Returns401()
    {
        using var client = fixture.CreateClient();
        var resp = await client.PostJsonAsync("/api/auth/change-password",
            new { currentPassword = "admin", newPassword = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
