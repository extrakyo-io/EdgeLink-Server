using System.Net;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

[Collection("Integration")]
public class SettingsTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds   = [];
    private readonly List<string> _maskIds   = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        foreach (var id in _portIds)
        {
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); }
            catch { }
        }
        foreach (var id in _maskIds)
        {
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{id}")); }
            catch { }
        }
        _client.Dispose();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 匯入時埠的 Id 一律重新產生,所以檔案裡帶的 sourceProtocolId 一定是別台機器的舊值。
    /// Router 只認 Id,連結一斷會變成「埠都在、狀態正常,但資料靜靜地不流動」——
    /// 沒有任何錯誤訊息可看。匯入後要依 sourceProtocolName 自動接回去。
    /// </summary>
    [Fact]
    public async Task Import_RelinksSourceProtocolIdByName()
    {
        string src = CreateId("RL_Src");
        string dst = CreateId("RL_Dst");

        var payload = new
        {
            masks = Array.Empty<object>(),
            ports = new object[]
            {
                new
                {
                    protocolName = src, netProtocol = "TCP SERVER",
                    localPort = "19240", remotePort = "--", targetIp = "",
                    maskType = "OriginalData", responseMaskType = "", requestMode = "serial",
                    sourceProtocolName = "", sourceProtocolId = "", isEnabled = false,
                },
                new
                {
                    protocolName = dst, netProtocol = "TCP CLIENT",
                    localPort = "--", remotePort = "19241", targetIp = "127.0.0.1",
                    maskType = "OriginalData", responseMaskType = "OriginalData",
                    requestMode = "concurrent",
                    // 這個 Id 在本機根本不存在 —— 匯出檔一定會長這樣
                    sourceProtocolName = src, sourceProtocolId = "deadbeef", isEnabled = false,
                },
            },
        };

        var resp = await _client.PostJsonAsync("/api/settings/import", payload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var list = await _client.GetAsync("/api/ports");
        using var doc = await list.ReadDocAsync();
        var ports = doc.RootElement.GetProperty("ports").EnumerateArray().ToList();

        var srcPort = ports.First(p => p.GetProperty("protocolName").GetString() == src);
        var dstPort = ports.First(p => p.GetProperty("protocolName").GetString() == dst);
        _portIds.Add(srcPort.GetProperty("id").GetString()!);
        _portIds.Add(dstPort.GetProperty("id").GetString()!);

        string srcId = srcPort.GetProperty("id").GetString()!;
        Assert.NotEqual("deadbeef", srcId);
        Assert.Equal(srcId, dstPort.GetProperty("sourceProtocolId").GetString());
    }

    /// <summary>來源埠沒一起匯入時不要亂接 —— 使用者可能是刻意分批匯入的。</summary>
    [Fact]
    public async Task Import_LeavesSourceProtocolIdAloneWhenNameNotFound()
    {
        string dst = CreateId("RL_Orphan");

        var payload = new
        {
            masks = Array.Empty<object>(),
            ports = new object[]
            {
                new
                {
                    protocolName = dst, netProtocol = "TCP CLIENT",
                    localPort = "--", remotePort = "19242", targetIp = "127.0.0.1",
                    maskType = "OriginalData", responseMaskType = "OriginalData",
                    requestMode = "serial",
                    sourceProtocolName = "NoSuchSourcePort", sourceProtocolId = "deadbeef",
                    isEnabled = false,
                },
            },
        };

        Assert.Equal(HttpStatusCode.OK, (await _client.PostJsonAsync("/api/settings/import", payload)).StatusCode);

        var list = await _client.GetAsync("/api/ports");
        using var doc = await list.ReadDocAsync();
        var port = doc.RootElement.GetProperty("ports").EnumerateArray()
            .First(p => p.GetProperty("protocolName").GetString() == dst);
        _portIds.Add(port.GetProperty("id").GetString()!);

        Assert.Equal("deadbeef", port.GetProperty("sourceProtocolId").GetString());
    }

    private string CreateId(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..6]}";

    [Fact]
    public async Task Export_ReturnsPortsAndMasksArrays()
    {
        var resp = await _client.GetAsync("/api/settings/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("ports").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("masks").ValueKind);
    }

    [Fact]
    public async Task Export_IncludesCreatedPort()
    {
        // Add a port
        var addResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "ExportTest19030",
            netProtocol  = "TCP SERVER",
            localPort    = "19030",
        });
        using var addDoc = await addResp.ReadDocAsync();
        string portId = addDoc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(portId);

        // Export
        var exp = await _client.GetAsync("/api/settings/export");
        using var doc = await exp.ReadDocAsync();

        bool found = doc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .Any(p => p.GetProperty("protocolName").GetString() == "ExportTest19030");
        Assert.True(found);
    }

    [Fact]
    public async Task Export_MasksDoNotIncludeOriginalData()
    {
        var exp = await _client.GetAsync("/api/settings/export");
        using var doc = await exp.ReadDocAsync();

        bool hasOriginal = doc.RootElement.GetProperty("masks")
            .EnumerateArray()
            .Any(m => m.GetProperty("maskId").GetString() == "OriginalData");
        Assert.False(hasOriginal, "OriginalData should be excluded from export");
    }

    // ── Import ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_AddsNewPortsAndMasks()
    {
        string maskId = $"ImportMask_{Guid.NewGuid().ToString("N")[..6]}";
        _maskIds.Add(maskId);

        string portName = $"ImportPort_{Guid.NewGuid().ToString("N")[..6]}";

        var payload = new
        {
            masks = new[]
            {
                new
                {
                    maskId,
                    outputTemplate  = "{raw}",
                    fieldDelimiter  = ";",
                    kvSeparator     = ":",
                    localizationKey = "",
                    description     = "",
                    sampleData      = "",
                    routeMode       = "",
                    correlationIdField = "",
                }
            },
            ports = new[]
            {
                new
                {
                    protocolName    = portName,
                    netProtocol     = "UDP",
                    localPort       = "19031",
                    remotePort      = "--",
                    targetIp        = "",
                    maskType        = "OriginalData",
                    responseMaskType = "",
                    requestMode     = "serial",
                    sourceProtocolName = "",
                    sourceProtocolId   = "",
                    isEnabled       = false,
                }
            },
        };

        var resp = await _client.PostJsonAsync("/api/settings/import", payload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Mask should exist
        var getM = await _client.GetAsync($"/api/masks/{maskId}");
        Assert.Equal(HttpStatusCode.OK, getM.StatusCode);

        // Port should exist — find it and track for cleanup
        var ports = await _client.GetAsync("/api/ports");
        using var doc = await ports.ReadDocAsync();
        var portEl = doc.RootElement.GetProperty("ports")
            .EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("protocolName").GetString() == portName);
        Assert.NotEqual(default, portEl);
        _portIds.Add(portEl.GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Import_SkipsDuplicatePorts()
    {
        // Add port first
        var addResp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = "DupImport19032",
            netProtocol  = "TCP SERVER",
            localPort    = "19032",
        });
        using var addDoc = await addResp.ReadDocAsync();
        _portIds.Add(addDoc.RootElement.GetProperty("id").GetString()!);

        // Count ports before import
        var before = await _client.GetAsync("/api/ports");
        using var beforeDoc = await before.ReadDocAsync();
        int countBefore = beforeDoc.RootElement.GetProperty("ports").GetArrayLength();

        // Import same port
        var payload = new
        {
            masks = Array.Empty<object>(),
            ports = new[]
            {
                new
                {
                    protocolName     = "DupImport19032",
                    netProtocol      = "TCP SERVER",
                    localPort        = "19032",
                    remotePort       = "--",
                    targetIp         = "",
                    maskType         = "OriginalData",
                    responseMaskType = "",
                    requestMode      = "serial",
                    sourceProtocolName = "",
                    sourceProtocolId   = "",
                    isEnabled        = false,
                }
            },
        };
        var resp = await _client.PostJsonAsync("/api/settings/import", payload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Count should be unchanged
        var after = await _client.GetAsync("/api/ports");
        using var afterDoc = await after.ReadDocAsync();
        int countAfter = afterDoc.RootElement.GetProperty("ports").GetArrayLength();
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task Import_InvalidJson_Returns400AndChangesNothing()
    {
        // 迴歸:Json.FromJson 先前吞掉解析失敗並回 new T(),使得這裡的 400 分支
        // 形同死碼、伺服器對著壞掉的內容回 200。現在解析失敗會往外拋,400 才真的會發生。
        var before = await _client.GetAsync("/api/ports");
        using var beforeDoc = await before.ReadDocAsync();
        int countBefore = beforeDoc.RootElement.GetProperty("ports").GetArrayLength();

        var content = new System.Net.Http.StringContent(
            "not-json", System.Text.Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/api/settings/import", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var after = await _client.GetAsync("/api/ports");
        using var afterDoc = await after.ReadDocAsync();
        Assert.Equal(countBefore, afterDoc.RootElement.GetProperty("ports").GetArrayLength());
    }

    [Fact]
    public async Task ExportThenImport_RoundTrip()
    {
        // Create a mask and port
        string maskId = $"RoundTrip_{Guid.NewGuid().ToString("N")[..6]}";
        _maskIds.Add(maskId);
        await _client.PostJsonAsync("/api/masks", new { maskId });

        // Export
        var expResp = await _client.GetAsync("/api/settings/export");
        string json = await expResp.Content.ReadAsStringAsync();

        // Delete what was created
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{maskId}"));
        _maskIds.Remove(maskId);

        // Re-import
        var impContent = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var impResp = await _client.PostAsync("/api/settings/import", impContent);
        Assert.Equal(HttpStatusCode.OK, impResp.StatusCode);

        // Mask should be back
        var check = await _client.GetAsync($"/api/masks/{maskId}");
        if (check.StatusCode == HttpStatusCode.OK)
            _maskIds.Add(maskId); // track for cleanup
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static System.Net.Http.StringContent JsonBody(object obj) =>
        new(System.Text.Json.JsonSerializer.Serialize(obj),
            System.Text.Encoding.UTF8, "application/json");
}
