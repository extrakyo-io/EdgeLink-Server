using System.Collections.Generic;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

public class BinaryValueMapTests
{
    // 一個位址上同時宣告「原始碼」與「查表文字」兩個欄位 —— 現場要的是兩者都有:
    // 文字給人看,原始碼才對得上驅動器面板。
    private static BinarySpec ErrSpec() => new()
    {
        byteOrder = "little",
        discriminator = new BinaryFieldRef { offset = 0, type = "u8" },
        maps = new Dictionary<string, Dictionary<string, string>>
        {
            ["driveErr"] = new()
            {
                ["0"]             = "NONE_OR_UNCODED",
                ["0x0207-0x0249"] = "PR_PARAM",
                ["0x8611"]        = "FOLLOWING_ERROR",
            },
            ["axis"] = new() { ["-1"] = "NA", ["0"] = "A", ["1"] = "B", ["2"] = "C" },
            ["flag"] = new() { ["0"] = "OFF", ["1"] = "ON" },
        },
        variants =
        [
            new BinaryVariant {
                match = 1, length = 8,
                template = "raw:{raw};txt:{txt};ax:{ax};fl:{fl};bits:{bits}",
                fields =
                [
                    new(){ name="mt",   offset=0, type="u8" },
                    new(){ name="raw",  offset=1, type="u16" },
                    new(){ name="txt",  offset=1, type="u16", mapRef="driveErr", mapDefault="UNKNOWN" },
                    new(){ name="ax",   offset=3, type="i8",  mapRef="axis" },
                    new(){ name="fl",   offset=4, type="bit", bit=0, mapRef="flag" },
                    new(){ name="bits", offset=4, type="bitrange", bit=1, count=2, mapRef="axis" },
                ]
            },
        ]
    };

    private static byte[] Packet(ushort err, sbyte axis, byte flags)
    {
        var p = new byte[8];
        p[0] = 1;
        p[1] = (byte)(err & 0xFF);
        p[2] = (byte)(err >> 8);
        p[3] = unchecked((byte)axis);
        p[4] = flags;
        return p;
    }

    [Fact]
    public void Decode_LooksUpExactHexAndDecimalKeys()
    {
        var spec = ErrSpec();
        Assert.Contains("txt:FOLLOWING_ERROR",  BinaryMaskDecoder.Decode(Packet(0x8611, 0, 0), spec));
        Assert.Contains("txt:NONE_OR_UNCODED",  BinaryMaskDecoder.Decode(Packet(0, 0, 0), spec));
        Assert.Contains("raw:34321",            BinaryMaskDecoder.Decode(Packet(0x8611, 0, 0), spec));  // 原始碼仍保留
    }

    /// <summary>驅動器異警表有一段是連續區間(PR 命令參數錯誤),不能只支援精確值。</summary>
    [Fact]
    public void Decode_LooksUpRangeKeys()
    {
        var spec = ErrSpec();
        Assert.Contains("txt:PR_PARAM", BinaryMaskDecoder.Decode(Packet(0x0207, 0, 0), spec));   // 下界
        Assert.Contains("txt:PR_PARAM", BinaryMaskDecoder.Decode(Packet(0x0231, 0, 0), spec));   // 區間內
        Assert.Contains("txt:PR_PARAM", BinaryMaskDecoder.Decode(Packet(0x0249, 0, 0), spec));   // 上界
        Assert.Contains("txt:UNKNOWN",  BinaryMaskDecoder.Decode(Packet(0x0250, 0, 0), spec));   // 出界
    }

    /// <summary>detailAxis 的 -1 是有意義的值(不適用),不能被當成範圍分隔符。</summary>
    [Fact]
    public void Decode_NegativeKeyIsNotParsedAsRange()
    {
        var spec = ErrSpec();
        Assert.Contains("ax:NA", BinaryMaskDecoder.Decode(Packet(0, -1, 0), spec));
        Assert.Contains("ax:B",  BinaryMaskDecoder.Decode(Packet(0, 1, 0), spec));
    }

    /// <summary>沒設 mapDefault 時對不到就輸出原始數值,不能變成空字串把整包吃掉。</summary>
    [Fact]
    public void Decode_FallsBackToRawNumberWhenNoMapDefault()
    {
        var spec = ErrSpec();
        string? outp = BinaryMaskDecoder.Decode(Packet(0, 7, 0), spec);   // axis=7 不在表裡,且沒設 mapDefault
        Assert.NotNull(outp);
        Assert.Contains("ax:7", outp);
    }

    [Fact]
    public void Decode_MapsBitAndBitrangeValues()
    {
        var spec = ErrSpec();
        Assert.Contains("fl:OFF", BinaryMaskDecoder.Decode(Packet(0, 0, 0b000), spec));
        Assert.Contains("fl:ON",  BinaryMaskDecoder.Decode(Packet(0, 0, 0b001), spec));
        Assert.Contains("bits:C", BinaryMaskDecoder.Decode(Packet(0, 0, 0b100), spec));   // bit1..2 = 2 → "C"
    }

    /// <summary>查表欄位在編碼時要被跳過(多對一反查不回去),但同位址的原始欄位照寫。</summary>
    [Fact]
    public void Encode_SkipsMappedFieldsAndStillWritesRaw()
    {
        var spec = ErrSpec();
        var kv = new Dictionary<string, string>
        {
            ["mt"] = "1", ["raw"] = "34321", ["ax"] = "1", ["fl"] = "1", ["bits"] = "2",
            // txt 故意不給 —— 它是衍生輸出,不該被當成必填欄位而讓整包被丟掉
        };

        byte[]? p = BinaryMaskEncoder.Encode(kv, spec);
        Assert.NotNull(p);
        Assert.Equal(0x11, p![1]);       // 34321 = 0x8611,little-endian
        Assert.Equal(0x86, p[2]);
    }

    [Fact]
    public void Validator_RejectsUnknownMapRefAndBadKeys()
    {
        var spec = ErrSpec();
        Assert.Null(BinarySpecValidator.Validate(spec));

        var missing = ErrSpec();
        missing.variants[0].fields[2].mapRef = "noSuchTable";
        Assert.Contains("mapRef", BinarySpecValidator.Validate(missing));

        var floatMap = ErrSpec();
        floatMap.variants[0].fields[2].type = "f32";
        Assert.Contains("mapRef", BinarySpecValidator.Validate(floatMap));

        var badKey = ErrSpec();
        badKey.maps!["driveErr"]["not-a-number"] = "x";
        Assert.Contains("key", BinarySpecValidator.Validate(badKey));
    }
}
