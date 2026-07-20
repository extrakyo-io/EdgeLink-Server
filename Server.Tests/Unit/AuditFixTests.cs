using System.Text.Json;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Modbus;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>全面稽核後修掉的問題的迴歸測試。</summary>
public class AuditFixTests
{
    // ── Json.FromJson 不再吞掉解析失敗 ────────────────────────────────────────
    // 先前是 catch { return new T(); },造成三個連鎖問題:
    //   1. 壞掉的設定檔被當成「空物件」載入,下次存檔就把使用者資料覆蓋掉(永久遺失)
    //   2. 各 API handler 的 try/catch → 400 "Invalid JSON" 形同死碼
    //   3. POST /api/ports/{id}/enabled 的 body 若截斷 → enabled=false → 靜默停用線上埠

    public class Poco { public int a; }

    [Fact]
    public void FromJson_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => Json.FromJson<Poco>("{ \"a\": "));
    }

    [Fact]
    public void FromJson_TypeMismatch_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => Json.FromJson<Poco>("{\"a\":\"not-an-int\"}"));
    }

    [Fact]
    public void FromJson_ValidJson_StillDeserialises()
    {
        var p = Json.FromJson<Poco>("{\"a\":42}");
        Assert.NotNull(p);
        Assert.Equal(42, p!.a);
    }

    // ── Modbus:32 位元型別需要 2 個暫存器 ───────────────────────────────────
    // Quantity 預設為 1 且先前不依 DataType 補足,導致 float32/int32/uint32
    // 只讀回 1 個暫存器 → RegistersToString 回空字串 → 該欄位被靜默丟棄,
    // 使用者看不到任何錯誤,只覺得「這個欄位怎麼都沒值」。

    [Theory]
    [InlineData("float32", 2)]
    [InlineData("uint32",  2)]
    [InlineData("int32",   2)]
    [InlineData("FLOAT32", 2)]   // 大小寫不敏感
    [InlineData("uint16",  1)]
    [InlineData("int16",   1)]
    [InlineData("bits",    1)]
    [InlineData(null,      1)]   // 未指定 → 視為 uint16
    public void RequiredRegisters_MatchesDataTypeWidth(string? dataType, int expected)
    {
        Assert.Equal(expected, ModbusTcpMasterConnector.RequiredRegisters(dataType));
    }
}
