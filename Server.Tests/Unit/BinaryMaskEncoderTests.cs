using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

public class BinaryMaskEncoderTests
{
    // 平台 TCP V1 的出站兩種訊息(16=移動命令 67 bytes、17=管理命令 21 bytes)
    private static BinarySpec PlatformSpec() => new()
    {
        byteOrder = "little",
        sync = "4f4b01",
        discriminator = new BinaryFieldRef { offset = 3, type = "u8" },
        variants =
        [
            new BinaryVariant {
                match = 16, length = 67,
                template = "mt:{mt};seq:{seq};mode:{mode}",
                fields =
                [
                    new(){ name="mt",   offset=3,  type="u8" },
                    new(){ name="len",  offset=5,  type="u16", auto="frameLength" },
                    new(){ name="seq",  offset=7,  type="u32", auto="seq" },
                    new(){ name="ts",   offset=11, type="u64", auto="timeMs" },
                    new(){ name="mode", offset=19, type="u8" },
                    new(){ name="rqa",  offset=43, type="f32", optional=true },
                    new(){ name="rqb",  offset=47, type="f32", optional=true },
                    new(){ name="rqc",  offset=51, type="f32", optional=true },
                    new(){ name="vel",  offset=55, type="f32", optional=true },
                ]
            },
            new BinaryVariant {
                match = 17, length = 21,
                template = "mt:{mt};seq:{seq};act:{act}",
                fields =
                [
                    new(){ name="mt",  offset=3,  type="u8" },
                    new(){ name="len", offset=5,  type="u16", auto="frameLength" },
                    new(){ name="seq", offset=7,  type="u32", auto="seq" },
                    new(){ name="ts",  offset=11, type="u64", auto="timeMs" },
                    new(){ name="act", offset=19, type="u8" },
                ]
            },
        ]
    };

    private static Dictionary<string, string> Kv(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Encode_WritesSyncDiscriminatorAndAutoFields()
    {
        var spec = PlatformSpec();
        var seq  = new BinarySeqCounters();

        byte[]? p = BinaryMaskEncoder.Encode(
            Kv(("mt", "16"), ("mode", "1"), ("rqa", "120.5"), ("rqb", "118.25"), ("rqc", "119")), spec, seq);

        Assert.NotNull(p);
        Assert.Equal(67, p!.Length);

        Assert.Equal(0x4F, p[0]);                                              // 'O'
        Assert.Equal(0x4B, p[1]);                                              // 'K'
        Assert.Equal(1,    p[2]);                                              // version
        Assert.Equal(16,   p[3]);                                              // msgType(由 variant.match 寫入)
        Assert.Equal(0,    p[4]);                                              // kind:沒宣告 → 維持 0
        Assert.Equal(67,   BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(5, 2)));   // frameLength
        Assert.Equal(1u,   BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(7, 4)));   // seq 第一筆 = 1

        long ts = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(11, 8));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(ts, now - 60_000, now + 60_000);

        Assert.Equal(1,       p[19]);                                          // mode
        Assert.Equal(120.5f,  BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(43, 4)));
        Assert.Equal(118.25f, BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(47, 4)));
        Assert.Equal(119f,    BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(51, 4)));
        Assert.Equal(0f,      BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(55, 4)));  // vel 缺 → 0(沿用預設)
    }

    /// <summary>seq 是每個 msgType 各一條 counter,而不是一條全域 counter。</summary>
    [Fact]
    public void Encode_SeqIsPerMessageTypeAndIncrements()
    {
        var spec = PlatformSpec();
        var seq  = new BinarySeqCounters();

        uint SeqOf(byte[]? p) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(7, 4));

        var move1 = BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "1")), spec, seq);
        var move2 = BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "1")), spec, seq);
        var mgmt1 = BinaryMaskEncoder.Encode(Kv(("mt", "17"), ("act", "3")),  spec, seq);
        var move3 = BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "1")), spec, seq);

        Assert.Equal(1u, SeqOf(move1));
        Assert.Equal(2u, SeqOf(move2));
        Assert.Equal(1u, SeqOf(mgmt1));   // 17 自己的 counter,不受 16 影響
        Assert.Equal(3u, SeqOf(move3));

        // 連線重建 → 兩條 counter 都回到 1(對端是以新連線重置期望值來驗收的)
        seq.Reset();
        Assert.Equal(1u, SeqOf(BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "1")), spec, seq)));
        Assert.Equal(1u, SeqOf(BinaryMaskEncoder.Encode(Kv(("mt", "17"), ("act", "1")),  spec, seq)));
    }

    /// <summary>必填欄位缺漏要整包丟棄,不能靜默送出一筆被歸零的命令。</summary>
    [Fact]
    public void Encode_DropsPacketWhenRequiredFieldMissing()
    {
        var spec = PlatformSpec();

        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "16")), spec));                  // 缺 mode(必填)
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "17")), spec));                  // 缺 act(必填)
        Assert.NotNull(BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "0")), spec)); // optional 缺沒關係
    }

    [Fact]
    public void Encode_DropsPacketOnUnknownOrMissingDiscriminator()
    {
        var spec = PlatformSpec();

        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mode", "1")), spec));               // 沒有 mt
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "19"), ("mode", "1")), spec)); // 沒有這個 variant
    }

    [Fact]
    public void Encode_DropsPacketOnUnparseableValue()
    {
        var spec = PlatformSpec();
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "abc")), spec));
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "16"), ("mode", "1"), ("rqa", "NaN")), spec));
    }

    /// <summary>編碼要是解碼的逆運算 —— 包含 scale/add 的反推。</summary>
    [Fact]
    public void Encode_RoundTripsThroughDecoder()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little",
            sync = "4f4b01",
            discriminator = new BinaryFieldRef { offset = 3, type = "u8" },
            variants =
            [
                new BinaryVariant {
                    match = 7, length = 16,
                    template = "mt:{mt};n:{n};big:{big};flag:{flag};bits:{bits};temp:{temp}",
                    fields =
                    [
                        new(){ name="mt",   offset=3,  type="u8" },
                        new(){ name="n",    offset=4,  type="i16" },
                        new(){ name="big",  offset=6,  type="u64" },
                        new(){ name="flag", offset=14, type="bit", bit=3 },
                        new(){ name="bits", offset=14, type="bitrange", bit=4, count=3 },
                        // 解碼是 raw*0.5 - 20,編碼要反推回 raw((23.5+20)/0.5 = 87,塞得進 u8)
                        new(){ name="temp", offset=15, type="u8", scale=0.5, add=-20.0, format="0.#" },
                    ]
                },
            ]
        };

        var kv = Kv(("mt", "7"), ("n", "-1234"), ("big", "9007199254740993"),
                    ("flag", "1"), ("bits", "5"), ("temp", "23.5"));

        byte[]? packet = BinaryMaskEncoder.Encode(kv, spec);
        Assert.NotNull(packet);

        string? decoded = BinaryMaskDecoder.Decode(packet, spec);
        // big 超過 2^53:走 double 會變成 9007199254740992,這裡必須原封不動回來
        Assert.Equal("mt:7;n:-1234;big:9007199254740993;flag:1;bits:5;temp:23.5", decoded);
    }

    /// <summary>反推出來的 raw 塞不進目標型別時要丟包,不能無聲截斷成另一個數值。</summary>
    [Fact]
    public void Encode_DropsPacketWhenValueOverflowsTargetType()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little",
            discriminator = new BinaryFieldRef { offset = 0, type = "u8" },
            variants =
            [
                new BinaryVariant {
                    match = 1, length = 4,
                    template = "mt:{mt};small:{small};scaled:{scaled}",
                    fields =
                    [
                        new(){ name="mt",     offset=0, type="u8" },
                        new(){ name="small",  offset=1, type="u8" },
                        // raw = (v + 40) / 0.1 —— v=23.5 會反推成 635,遠超過 u8
                        new(){ name="scaled", offset=2, type="u8", scale=0.1, add=-40.0 },
                    ]
                },
            ]
        };

        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "1"), ("small", "300"), ("scaled", "-35")), spec));
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "1"), ("small", "-1"),  ("scaled", "-35")), spec));
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "1"), ("small", "10"),  ("scaled", "23.5")), spec));
        Assert.NotNull(BinaryMaskEncoder.Encode(Kv(("mt", "1"), ("small", "255"), ("scaled", "-35")), spec));
    }

    [Fact]
    public void Validator_RejectsBadAutoSpec()
    {
        BinarySpec WithAuto(string auto, string type) => new()
        {
            byteOrder = "little",
            variants =
            [
                new BinaryVariant {
                    match = 1, length = 8, template = "a:{a}",
                    fields = [ new(){ name="a", offset=0, type=type, auto=auto } ]
                },
            ]
        };

        Assert.Contains("auto", BinarySpecValidator.Validate(WithAuto("nonsense", "u32")));
        Assert.Contains("整數",  BinarySpecValidator.Validate(WithAuto("seq", "f32")));
        Assert.Null(BinarySpecValidator.Validate(WithAuto("seq", "u32")));
        Assert.Null(BinarySpecValidator.Validate(WithAuto("timeMs", "u64")));
    }
}
