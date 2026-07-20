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

    // ── #15 framer 與 decoder 的 discriminator 解讀必須一致 ──────────────────

    /// <summary>framer 先前自己有一份 ReadInt,把 i8 當成無號:0xF0 得 240 而非 -16,
    /// 於是 match=-16 的 variant 永遠對不上,整包被丟掉(decoder 本來解得出來)。</summary>
    [Fact]
    public void Framer_SignedDiscriminator_MatchesDecoder()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little",
            discriminator = new BinaryFieldRef { offset = 0, type = "i8" },
            variants = new List<BinaryVariant> { new BinaryVariant { match = -16, length = 4 } }
        };

        var f = new BinaryStreamFramer(spec);
        f.Append(new byte[] { 0xF0, 0x11, 0x22, 0x33 });

        var pkt = f.Next();
        Assert.NotNull(pkt);
        Assert.Equal(4, pkt!.Length);
        Assert.Equal(0xF0, pkt[0]);
    }

    /// <summary>沒有 sync 時遇到未知 discriminator,先前是 `_len = 0` 把整個緩衝丟掉,
    /// 連排在後面「已經完整」的封包也一起賠掉。現在應只跳 1 個位元組重新對齊。</summary>
    [Fact]
    public void Framer_UnknownDiscriminatorWithoutSync_KeepsLaterCompletePacket()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little",
            discriminator = new BinaryFieldRef { offset = 0, type = "u8" },
            variants = new List<BinaryVariant> { new BinaryVariant { match = 1, length = 4 } }
        };

        var f = new BinaryStreamFramer(spec);
        // 前 4 個位元組是無法辨識的雜訊,後 4 個是完整的 match=1 封包
        f.Append(new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0x01, 0x11, 0x22, 0x33 });

        var pkt = f.Next();
        Assert.NotNull(pkt);
        Assert.Equal(new byte[] { 0x01, 0x11, 0x22, 0x33 }, pkt);
    }

    // ── #16 BinarySpec 在存檔當下就要驗證 ────────────────────────────────────

    private static BinarySpec SpecWith(BinaryField field, int length = 8) => new BinarySpec
    {
        byteOrder = "little",
        variants = new List<BinaryVariant>
        {
            new BinaryVariant { isDefault = true, length = length, template = "v:{v}",
                                fields = new List<BinaryField> { field } }
        }
    };

    [Fact]
    public void Validate_GoodSpec_ReturnsNull()
    {
        var spec = SpecWith(new BinaryField { name = "v", offset = 0, type = "u32", format = "0.###" });
        Assert.Null(BinarySpecValidator.Validate(spec));
    }

    [Fact]
    public void Validate_NullSpec_IsAllowed()   // 純文字 mask
        => Assert.Null(BinarySpecValidator.Validate(null));

    [Fact]
    public void Validate_FieldPastPacketLength_IsRejected()
    {
        var spec = SpecWith(new BinaryField { name = "v", offset = 6, type = "u32" }, length: 8);
        Assert.Contains("超出封包長度", BinarySpecValidator.Validate(spec));
    }

    /// <summary>整數專用的格式字串在 double 上會拋 FormatException —— 想 dump 十六進位
    /// 的人很自然會填 "X2",先前會存進去然後每包解碼都炸。</summary>
    [Fact]
    public void Validate_IntegerOnlyFormatString_IsRejected()
    {
        var spec = SpecWith(new BinaryField { name = "v", offset = 0, type = "u32", format = "X2" });
        Assert.Contains("format", BinarySpecValidator.Validate(spec));
    }

    [Fact]
    public void Validate_UnknownType_IsRejected()
    {
        var spec = SpecWith(new BinaryField { name = "v", offset = 0, type = "u24" });
        Assert.Contains("不認得", BinarySpecValidator.Validate(spec));
    }

    [Fact]
    public void Validate_BadByteOrder_IsRejected()
    {
        var spec = SpecWith(new BinaryField { name = "v", offset = 0, type = "u8" });
        spec.byteOrder = "middle";
        Assert.Contains("byteOrder", BinarySpecValidator.Validate(spec));
    }

    /// <summary>有 sync(代表要走 TCP 分包)時,length=0 的 variant 永遠框不出封包。</summary>
    [Fact]
    public void Validate_ZeroLengthVariantWithSync_IsRejected()
    {
        var spec = SpecWith(new BinaryField { name = "v", offset = 0, type = "u8" }, length: 0);
        spec.sync = "4f4b";
        Assert.Contains("length", BinarySpecValidator.Validate(spec));
    }
}
