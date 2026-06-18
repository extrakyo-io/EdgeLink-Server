using System.Net;
using System.Text;
using EdgeLink;
using EdgeLink.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EdgeLink.Tests;

/// <summary>
/// Starts a real EdgeLink server on port 18080 for the duration of the test session.
/// Cleans Setting/ and Data/ before start so all singletons initialise fresh.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public const int  HttpPort = 18080;
    public string     BaseUrl  => $"http://localhost:{HttpPort}";

    private IHost? _host;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        CleanTestData();

        string[] args = ["--port", HttpPort.ToString(), "--no-https"];

        _host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(b => b.ClearProviders())
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(AppConfig.FromArgs(args));
                services.AddHostedService<EdgeLinkService>();
            })
            .Build();

        await _host.StartAsync();
        await WaitForServerReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
        CleanTestData();
    }

    // ── Client helpers ───────────────────────────────────────────────────────

    /// <summary>Anonymous client (no auth cookie).</summary>
    public HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>Client already logged-in with the default "admin" password.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(string password = "admin")
    {
        var client = CreateClient();
        var resp   = await client.PostJsonAsync("/api/auth/login", new { password });
        resp.EnsureSuccessStatusCode();
        return client;
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private static void CleanTestData()
    {
        string root = AppContext.BaseDirectory;
        TryDeleteDir(Path.Combine(root, "Setting"));
        TryDeleteDir(Path.Combine(root, "Data"));
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* ignore */ }
    }

    private async Task WaitForServerReadyAsync()
    {
        using var client = new HttpClient();
        for (int i = 0; i < 40; i++)
        {
            try
            {
                var r = await client.GetAsync($"{BaseUrl}/api/auth/status");
                if (r.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(250);
        }
        throw new TimeoutException("EdgeLink server did not become ready within 10 s.");
    }
}

// ── xUnit collection so all integration tests share one server ───────────────

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<ServerFixture> { }

// ── HttpClient extension helpers ─────────────────────────────────────────────

public static class HttpClientExtensions
{
    private static readonly System.Text.Json.JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient c, string url, object body)
    {
        var json    = System.Text.Json.JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return c.PostAsync(url, content);
    }

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient c, string url, object body)
    {
        var json    = System.Text.Json.JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return c.PutAsync(url, content);
    }

    public static async Task<T> ReadJsonAsync<T>(this HttpResponseMessage r) where T : new()
    {
        string body = await r.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<T>(body, _opts) ?? new T();
    }

    public static async Task<System.Text.Json.JsonDocument> ReadDocAsync(this HttpResponseMessage r)
    {
        string body = await r.Content.ReadAsStringAsync();
        return System.Text.Json.JsonDocument.Parse(body);
    }
}
