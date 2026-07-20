using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using EdgeLink.Infrastructure;
using EdgeLink.Mask;
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

    // ── BinaryMaskDecoder 數值/位元正確性 ────────────────────────────────────

    private static BinarySpec OneField(BinaryField field, string template) => new BinarySpec
    {
        byteOrder = "little",
        variants = new List<BinaryVariant>
        {
            new BinaryVariant { isDefault = true, template = template, fields = new List<BinaryField> { field } }
        }
    };

    /// <summary>u64 先前全部經 double 中轉,而自訂數值格式在 double 上只保留 15 位
    /// 有效數字,所以 16 位數的計數器/序號會被靜默算錯(尾數被抹成 0)。</summary>
    [Fact]
    public void Decode_U64_KeepsFullPrecision()
    {
        var spec = OneField(new BinaryField { name = "v", offset = 0, type = "u64" }, "v:{v}");
        var d = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(d, 1000000000000001UL);

        Assert.Equal("v:1000000000000001", BinaryMaskDecoder.Decode(d, spec));
    }

    /// <summary>bit 索引 >= 8 應該往後推位元組。先前 d[offset] >> bit 會被 C# 的
    /// 位移取模規則吃掉,bit=12 永遠回 0。</summary>
    [Theory]
    [InlineData(12, "1")]   // byte1 = 0x10 的第 4 位
    [InlineData(11, "0")]
    [InlineData(4,  "0")]   // byte0 = 0x00
    public void Decode_BitIndexBeyondFirstByte_ReadsCorrectBit(int bit, string expected)
    {
        var spec = OneField(new BinaryField { name = "b", offset = 0, type = "bit", bit = bit }, "b:{b}");
        var d = new byte[] { 0x00, 0x10 };

        Assert.Equal($"b:{expected}", BinaryMaskDecoder.Decode(d, spec));
    }

    /// <summary>bitrange 先前只讀單一位元組且 count 被 clamp 到 8,跨位元組的欄位
    /// (例如 12 位元 ADC)會被靜默截斷。</summary>
    [Fact]
    public void Decode_BitRange_CrossesByteBoundary()
    {
        // bytes: 0xC0 = 1100_0000(bit6,bit7 = 1)、0x0F = 0000_1111(bit8..11 = 1)
        // 取 bit6 起算 4 個位元 → 1,1,1,1 → LSB first = 15(舊行為會回 3)
        var spec = OneField(
            new BinaryField { name = "r", offset = 0, type = "bitrange", bit = 6, count = 4 }, "r:{r}");
        var d = new byte[] { 0xC0, 0x0F };

        Assert.Equal("r:15", BinaryMaskDecoder.Decode(d, spec));
    }

    /// <summary>離譜的 offset 先前會讓界線檢查整數溢位而通過,接著 Slice 拋例外 ——
    /// 該例外會一路逸出到接收迴圈並讓整個埠停止收資料。現在應安靜丟棄該欄位。</summary>
    [Fact]
    public void Decode_AbsurdOffset_DoesNotThrow()
    {
        var spec = OneField(new BinaryField { name = "v", offset = int.MaxValue, type = "u64" }, "v:{v}");

        var outp = BinaryMaskDecoder.Decode(new byte[8], spec);
        Assert.True(string.IsNullOrEmpty(outp));   // 欄位缺失 → template 丟棄整筆
    }
}
