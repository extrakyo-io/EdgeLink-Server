using System.Buffers.Binary;
using System.Collections.Generic;
using EdgeLink.Infrastructure;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

public class BinaryMaskDecoderTests
{
    // ── sync(magic)驗證 ─────────────────────────────────────────────────────

    private static BinarySpec SyncSpec(string sync) => new()
    {
        byteOrder = "little",
        sync = sync,
        discriminator = new BinaryFieldRef { offset = 3, type = "u8" },
        variants =
        [
            new BinaryVariant {
                match = 1, length = 8, template = "v:{v}",
                fields = [ new(){ name="v", offset=4, type="u32" } ]
            },
        ]
    };

    private static byte[] SyncPacket(byte m0, byte m1, byte ver)
        => [m0, m1, ver, 1, 0x2A, 0, 0, 0];

    /// <summary>
    /// UDP 沒有分包步驟,整個 datagram 直接進解碼器 —— 先前完全不驗 magic,
    /// 任何長度湊巧相同、discriminator 湊巧對上的雜訊都會被解成合法資料轉發下去。
    /// TCP 那條靠 framer 對齊看不出這個洞。
    /// </summary>
    [Fact]
    public void Decode_RejectsPacketWhoseSyncDoesNotMatch()
    {
        var spec = SyncSpec("4f4b01");

        Assert.Equal("v:42", BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x4B, 1), spec));
        Assert.Null(BinaryMaskDecoder.Decode(SyncPacket(0x58, 0x4B, 1), spec));   // magic0 錯
        Assert.Null(BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x58, 1), spec));   // magic1 錯
        Assert.Null(BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x4B, 2), spec));   // version 錯
    }

    /// <summary>sync 只涵蓋前兩個位元組時,第三個位元組(version)不該被連坐檢查。</summary>
    [Fact]
    public void Decode_OnlyChecksTheBytesSyncCovers()
    {
        var spec = SyncSpec("4f4b");
        Assert.Equal("v:42", BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x4B, 1), spec));
        Assert.Equal("v:42", BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x4B, 7), spec));
        Assert.Null(BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x58, 1), spec));
    }

    /// <summary>沒設 sync 的 mask 維持原本行為(不檢查),否則既有設定會全部失效。</summary>
    [Fact]
    public void Decode_SkipsSyncCheckWhenNotConfigured()
    {
        var spec = SyncSpec("");
        Assert.Equal("v:42", BinaryMaskDecoder.Decode(SyncPacket(0x4F, 0x4B, 1), spec));
        Assert.Equal("v:42", BinaryMaskDecoder.Decode(SyncPacket(0xAA, 0xBB, 9), spec));
    }

    /// <summary>封包比 sync 還短一定不是這個協定的東西。</summary>
    [Fact]
    public void Decode_RejectsPacketShorterThanSync()
    {
        var spec = SyncSpec("4f4b01");
        Assert.Null(BinaryMaskDecoder.Decode(new byte[] { 0x4F, 0x4B }, spec));
    }

    // ── OK-protocol spec(對應 RigBinary)────────────────────────────────────
    private static BinarySpec OkSpec() => new BinarySpec
    {
        byteOrder = "little",
        discriminator = new BinaryFieldRef { offset = 3, type = "u8" },
        variants = new List<BinaryVariant>
        {
            new BinaryVariant {
                match = 1, length = 37,
                template = "id:{id};seq:{seq};conn:{conn};jlx:{jlx};jly:{jly};jlf:{jlf};jlst:{jlst};jlraw:{jlraw};jrx:{jrx};jry:{jry};jrf:{jrf}",
                fields = new List<BinaryField> {
                    new(){ name="id", type="const", value="rig1" },
                    new(){ name="seq", offset=5, type="u32" },
                    new(){ name="conn", offset=17, type="u8" },
                    new(){ name="jlx", offset=19, type="f32", format="0.###" },
                    new(){ name="jly", offset=23, type="f32", format="0.###" },
                    new(){ name="jlf", offset=27, type="bitrange", bit=0, count=2 },
                    new(){ name="jlst", offset=27, type="bit", bit=2 },
                    new(){ name="jlraw", offset=27, type="bit", bit=3 },
                    new(){ name="jrx", offset=28, type="f32", format="0.###" },
                    new(){ name="jry", offset=32, type="f32", format="0.###" },
                    new(){ name="jrf", offset=36, type="bitrange", bit=0, count=2 },
                }
            },
            new BinaryVariant {
                match = 3, length = 31,
                template = "id:{id};seq:{seq};e1p:{e1p};e1deg:{e1deg};e1gm:{e1gm};e1st:{e1st}",
                fields = new List<BinaryField> {
                    new(){ name="id", type="const", value="rig1" },
                    new(){ name="seq", offset=5, type="u32" },
                    new(){ name="e1p", offset=19, type="u8" },
                    new(){ name="e1gm", offset=20, type="bit", bit=0 },
                    new(){ name="e1st", offset=20, type="bit", bit=1 },
                    new(){ name="e1deg", offset=21, type="f32", format="0.#" },
                }
            },
        }
    };

    private static byte[] Joystick(uint seq, float jlx, float jly, byte jlStatus, float jrx, float jry, byte jrStatus)
    {
        var d = new byte[37];
        d[0] = (byte)'O'; d[1] = (byte)'K'; d[2] = 1; d[3] = 1; d[4] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(5), seq);
        d[17] = 2; d[18] = 2;
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(19), jlx);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(23), jly);
        d[27] = jlStatus;
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(28), jrx);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(32), jry);
        d[36] = jrStatus;
        return d;
    }

    private static Dictionary<string, string> Parse(string kv)
    {
        var f = new Dictionary<string, string>();
        foreach (var p in kv.Split(';'))
        {
            int i = p.IndexOf(':');
            if (i > 0) f[p[..i]] = p[(i + 1)..];
        }
        return f;
    }

    [Fact]
    public void Joystick_DecodesFloatsAndBits()
    {
        // jlStatus 0x0C = bit2(stale)+bit3(raw); jrStatus 0x03 = bits0-1 => jrf=3
        var pkt = Joystick(7, 0.5f, -0.5f, 0x0C, 0.1f, 0.2f, 0x03);
        var outp = BinaryMaskDecoder.Decode(pkt, OkSpec());
        Assert.NotNull(outp);
        var f = Parse(outp!);

        Assert.Equal("rig1", f["id"]);
        Assert.Equal("7", f["seq"]);
        Assert.Equal("2", f["conn"]);
        Assert.Equal("0.5", f["jlx"]);
        Assert.Equal("-0.5", f["jly"]);
        Assert.Equal("0", f["jlf"]);     // bits0-1 of 0x0C = 0
        Assert.Equal("1", f["jlst"]);    // bit2
        Assert.Equal("1", f["jlraw"]);   // bit3
        Assert.Equal("0.1", f["jrx"]);
        Assert.Equal("0.2", f["jry"]);
        Assert.Equal("3", f["jrf"]);     // bits0-1 of 0x03 = 3
    }

    [Fact]
    public void Encoder_DecodesViaDiscriminatorDispatch()
    {
        var d = new byte[31];
        d[0] = (byte)'O'; d[1] = (byte)'K'; d[2] = 1; d[3] = 3; d[4] = 0; // msgType=3
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(5), 8);
        d[17] = 2; d[18] = 2;
        d[19] = 100; d[20] = 0x02;                                        // e1p=100, status bit1=st
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(21), 141.5f);

        var outp = BinaryMaskDecoder.Decode(d, OkSpec());
        Assert.NotNull(outp);
        var f = Parse(outp!);
        Assert.Equal("8", f["seq"]);
        Assert.Equal("100", f["e1p"]);
        Assert.Equal("141.5", f["e1deg"]);
        Assert.Equal("0", f["e1gm"]);
        Assert.Equal("1", f["e1st"]);
    }

    [Fact]
    public void WrongLength_IsDropped()
    {
        var pkt = Joystick(1, 0f, 0f, 0, 0f, 0f, 0);
        var shortPkt = pkt[..30];               // 少於 37
        Assert.Null(BinaryMaskDecoder.Decode(shortPkt, OkSpec()));
    }

    [Fact]
    public void UnknownDiscriminator_IsDropped()
    {
        var pkt = Joystick(1, 0f, 0f, 0, 0f, 0f, 0);
        pkt[3] = 99;                             // 不存在的 msgType
        Assert.Null(BinaryMaskDecoder.Decode(pkt, OkSpec()));
    }

    [Fact]
    public void BigEndian_ReadsCorrectly()
    {
        var spec = new BinarySpec
        {
            byteOrder = "big",
            variants = new List<BinaryVariant> {
                new(){ isDefault = true, template = "v:{v}",
                       fields = new List<BinaryField> { new(){ name="v", offset=0, type="u16" } } }
            }
        };
        var outp = BinaryMaskDecoder.Decode(new byte[] { 0x01, 0x02 }, spec); // BE 0x0102 = 258
        Assert.Equal("v:258", outp);
    }

    [Fact]
    public void JsonRoundTrip_PreservesBinarySpec()
    {
        // 確保 System.Text.Json(IncludeFields)能還原巢狀 binary
        var def = new MaskDefinition
        {
            maskId = "RigBinary", fieldDelimiter = ";", kvSeparator = ":",
            binary = OkSpec()
        };
        string json = Json.ToJson(def);
        var back = Json.FromJson<MaskDefinition>(json);
        Assert.NotNull(back!.binary);
        Assert.Equal(2, back.binary!.variants.Count);
        Assert.Equal(3, back.binary.discriminator!.offset);

        var pkt = Joystick(42, 0.25f, 0f, 0, 0f, 0f, 0);
        var outp = BinaryMaskDecoder.Decode(pkt, back.binary);
        Assert.Contains("seq:42", outp);
        Assert.Contains("jlx:0.25", outp);
    }
}
