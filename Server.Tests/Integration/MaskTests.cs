using System.Net;
using System.Text.Json;
using Xunit;

namespace EdgeLink.Tests.Integration;

[Collection("Integration")]
public class MaskTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _createdMasks = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        foreach (var id in _createdMasks)
        {
            try
            {
                await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{id}"));
            }
            catch { /* best-effort */ }
        }
        _client.Dispose();
    }

    // ── 二進位預覽(同一個端點雙向:hex → KV,KV → hex)──────────────────────────

    /// <summary>
    /// 編碼預覽走的是出站方向真正在用的那個編碼器,所以這裡看到的 hex 就是實際會上線的
    /// 位元組。設定 mask 時能先確認一遍,不必等到接上對端才發現欄位填錯。
    /// </summary>
    [Fact]
    public async Task PreviewBinary_EncodesKvToHexAndDecodesBack()
    {
        object spec = new
        {
            byteOrder     = "little",
            sync          = "4f4b01",
            discriminator = new { offset = 3, type = "u8" },
            variants = new object[]
            {
                new
                {
                    match = 17, isDefault = false, length = 21,
                    template = "mt:{mt};seq:{seq};ts:{ts};act:{act}",
                    fields = new object[]
                    {
                        new { name = "mt",  offset = 3,  type = "u8" },
                        new { name = "len", offset = 5,  type = "u16", auto = "frameLength" },
                        new { name = "seq", offset = 7,  type = "u32", auto = "seq" },
                        new { name = "ts",  offset = 11, type = "u64", auto = "timeMs" },
                        new { name = "act", offset = 19, type = "u8" },
                    },
                },
            },
        };

        // KV → wire
        var enc = await _client.PostJsonAsync("/api/masks/preview-binary",
            new { binary = spec, kv = "mt:17;act:3" });
        Assert.Equal(HttpStatusCode.OK, enc.StatusCode);
        using var encDoc = await enc.ReadDocAsync();
        Assert.False(encDoc.RootElement.GetProperty("dropped").GetBoolean());

        string hex = encDoc.RootElement.GetProperty("output").GetString()!;
        Assert.Equal(42, hex.Length);                       // 21 bytes
        Assert.StartsWith("4F4B0111", hex);                 // 'O' 'K' version=1 msgType=17
        Assert.Equal("1500", hex.Substring(10, 4));         // frameLength = 21,little-endian
        Assert.Equal("01000000", hex.Substring(14, 8));     // seq 從 1 起算

        // wire → KV(把剛編出來的 hex 丟回去解,欄位要對得上)
        var dec = await _client.PostJsonAsync("/api/masks/preview-binary",
            new { binary = spec, hex });
        using var decDoc = await dec.ReadDocAsync();
        string kv = decDoc.RootElement.GetProperty("output").GetString()!;
        Assert.Contains("mt:17", kv);
        Assert.Contains("seq:1", kv);
        Assert.Contains("act:3", kv);
    }

    /// <summary>必填欄位缺漏 → dropped,而不是送出一包被歸零的命令。</summary>
    [Fact]
    public async Task PreviewBinary_ReportsDroppedWhenRequiredFieldMissing()
    {
        object spec = new
        {
            byteOrder     = "little",
            discriminator = new { offset = 0, type = "u8" },
            variants = new object[]
            {
                new
                {
                    match = 5, isDefault = false, length = 3,
                    template = "mt:{mt};act:{act}",
                    fields = new object[]
                    {
                        new { name = "mt",  offset = 0, type = "u8" },
                        new { name = "act", offset = 1, type = "u8" },
                    },
                },
            },
        };

        var resp = await _client.PostJsonAsync("/api/masks/preview-binary",
            new { binary = spec, kv = "mt:5" });
        using var doc = await resp.ReadDocAsync();
        Assert.True(doc.RootElement.GetProperty("dropped").GetBoolean());
        Assert.Equal("", doc.RootElement.GetProperty("output").GetString());
    }

    // ── List masks ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMasks_AlwaysIncludesOriginalData()
    {
        var resp = await _client.GetAsync("/api/masks");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        var types = doc.RootElement.GetProperty("maskTypes");
        bool hasOriginal = types.EnumerateArray().Any(t => t.GetString() == "OriginalData");
        Assert.True(hasOriginal);
    }

    // ── Add mask ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddMask_NewId_Returns200Or201()
    {
        string id   = $"TestMask_{Guid.NewGuid().ToString("N")[..6]}";
        var resp    = await _client.PostJsonAsync("/api/masks", new { maskId = id });

        Assert.True(resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Created,
            $"Expected 200 or 201, got {(int)resp.StatusCode}");
        _createdMasks.Add(id);

        // Should appear in list
        var list = await _client.GetAsync("/api/masks");
        using var doc = await list.ReadDocAsync();
        bool found = doc.RootElement.GetProperty("maskTypes")
            .EnumerateArray().Any(t => t.GetString() == id);
        Assert.True(found);
    }

    [Fact]
    public async Task AddMask_DuplicateId_Returns400()
    {
        string id = $"DupMask_{Guid.NewGuid().ToString("N")[..6]}";
        await _client.PostJsonAsync("/api/masks", new { maskId = id });
        _createdMasks.Add(id);

        var r2 = await _client.PostJsonAsync("/api/masks", new { maskId = id });
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    [Fact]
    public async Task AddMask_EmptyId_Returns400()
    {
        var resp = await _client.PostJsonAsync("/api/masks", new { maskId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Get definition ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDefinition_OriginalData_Returns200()
    {
        var resp = await _client.GetAsync("/api/masks/OriginalData");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = await resp.ReadDocAsync();
        Assert.Equal("OriginalData", doc.RootElement.GetProperty("maskId").GetString());
        Assert.Equal("{raw}",        doc.RootElement.GetProperty("outputTemplate").GetString());
    }

    [Fact]
    public async Task GetDefinition_NonExistent_Returns404()
    {
        var resp = await _client.GetAsync("/api/masks/DoesNotExist12345");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Save (PUT) definition ─────────────────────────────────────────────────

    [Fact]
    public async Task SaveDefinition_UpdatesTemplate()
    {
        string id = $"SaveMask_{Guid.NewGuid().ToString("N")[..6]}";
        await _client.PostJsonAsync("/api/masks", new { maskId = id });
        _createdMasks.Add(id);

        var putResp = await _client.PutJsonAsync($"/api/masks/{id}", new
        {
            maskId          = id,
            outputTemplate  = "{val}",
            fieldDelimiter  = ";",
            kvSeparator     = ":",
            description     = "test",
            localizationKey = "",
            sampleData      = "",
            routeMode       = "",
            correlationIdField = "",
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Verify persisted
        var getResp = await _client.GetAsync($"/api/masks/{id}");
        using var doc = await getResp.ReadDocAsync();
        Assert.Equal("{val}", doc.RootElement.GetProperty("outputTemplate").GetString());
    }

    [Fact]
    public async Task SaveDefinition_IdMismatch_Returns400()
    {
        string id = $"IdMismatch_{Guid.NewGuid().ToString("N")[..6]}";
        await _client.PostJsonAsync("/api/masks", new { maskId = id });
        _createdMasks.Add(id);

        // Body contains different maskId
        var putResp = await _client.PutJsonAsync($"/api/masks/{id}", new
        {
            maskId         = "different_id",
            outputTemplate = "{x}",
        });
        Assert.Equal(HttpStatusCode.BadRequest, putResp.StatusCode);
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameMask_ChangesId()
    {
        string oldId = $"OldMask_{Guid.NewGuid().ToString("N")[..6]}";
        string newId = $"NewMask_{Guid.NewGuid().ToString("N")[..6]}";

        await _client.PostJsonAsync("/api/masks", new { maskId = oldId });
        _createdMasks.Add(newId);  // after rename the new ID needs cleanup

        var resp = await _client.PostJsonAsync($"/api/masks/{oldId}/rename", new { newId });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Old ID gone
        var getOld = await _client.GetAsync($"/api/masks/{oldId}");
        Assert.Equal(HttpStatusCode.NotFound, getOld.StatusCode);

        // New ID exists
        var getNew = await _client.GetAsync($"/api/masks/{newId}");
        Assert.Equal(HttpStatusCode.OK, getNew.StatusCode);
    }

    [Fact]
    public async Task RenameMask_NonExistent_Returns404()
    {
        var resp = await _client.PostJsonAsync("/api/masks/NoSuchMask/rename", new { newId = "whatever" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMask_RemovesFromList()
    {
        string id = $"DelMask_{Guid.NewGuid().ToString("N")[..6]}";
        await _client.PostJsonAsync("/api/masks", new { maskId = id });

        var delResp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{id}"));
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var getResp = await _client.GetAsync($"/api/masks/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    [Fact]
    public async Task DeleteMask_NonExistent_Returns404()
    {
        var resp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, "/api/masks/NoSuchMask12345"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
