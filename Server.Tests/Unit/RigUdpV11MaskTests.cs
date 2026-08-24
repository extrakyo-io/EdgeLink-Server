using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>
/// 用 docs/RigBinary.mask.json 這份**實際會匯入的** mask,對照
/// 「搖桿-編碼器-踏板-按鈕 UDP 資料格式 V1.1」逐欄驗證解碼。
///
/// 這裡刻意讀真實檔案而不是在測試裡另建一份 spec:mask 改壞了要在這裡就紅,
/// 而不是等到現場設備接上去才發現。
/// </summary>
public class RigUdpV11MaskTests
{
    private const byte MsgJoystick = 1, MsgButtons = 2, MsgEncoder = 3;

    private static BinarySpec Spec()
    {
        // 從 repo 根往上找 —— 測試的工作目錄是 bin/Debug/net8.0
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "RigBinary.mask.json")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 docs/RigBinary.mask.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "docs", "RigBinary.mask.json")));
        var binary = doc.RootElement.GetProperty("masks")[0].GetProperty("binary");

        // 走伺服器自己那組設定。BinarySpec 用的是 public 欄位而不是屬性,
        // System.Text.Json 預設不碰欄位 —— 少了 IncludeFields 會安靜地反序列化出
        // 一個 variants 全空的 spec,然後每一包都解不出來。驗的必須是生產路徑。
        var spec = EdgeLink.Infrastructure.Json.FromJson<BinarySpec>(binary.GetRawText());
        Assert.NotNull(spec);
        Assert.NotEmpty(spec!.variants);
        return spec;
    }

    /// <summary>共同標頭 19 bytes(規格 §4 的 17 bytes + §5 的 connState / unitCount)。</summary>
    private static byte[] Frame(byte msgType, uint seq, ulong ts, byte conn, byte units, params byte[] payload)
    {
        var buf = new byte[19 + payload.Length];
        buf[0] = 0x4F;                                   // 'O'
        buf[1] = 0x4B;                                   // 'K'
        buf[2] = 1;                                      // version
        buf[3] = msgType;
        buf[4] = 0;                                      // kind = 0 推送狀態
        BitConverter.GetBytes(seq).CopyTo(buf, 5);
        BitConverter.GetBytes(ts).CopyTo(buf, 9);
        buf[17] = conn;
        buf[18] = units;
        payload.CopyTo(buf, 19);
        return buf;
    }

    private static byte[] F32(float v) => BitConverter.GetBytes(v);

    /// <summary>解碼器回傳的是套過 template 的 KV 文字,這裡拆回欄位方便逐欄斷言。</summary>
    private static Dictionary<string, string> Decode(byte[] frame)
    {
        string? line = BinaryMaskDecoder.Decode(frame, Spec());
        Assert.NotNull(line);

        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in line!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = pair.IndexOf(':');
            if (i > 0) kv[pair[..i]] = pair[(i + 1)..];
        }
        return kv;
    }

    // ── 標頭 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 規格 §3:「不相容變更時 version +1,version 欄位不符即丟棄」。
    /// mask 的 sync 先前只有 magic 兩個 byte,version 完全沒被驗證 ——
    /// 對端升到 V2 之後我們會照樣把不相容的封包解出一堆看似正常的數值。
    /// </summary>
    [Fact]
    public void Version不符的封包要丟掉()
    {
        var ok = Frame(MsgJoystick, 1, 0, 2, 2, new byte[18]);
        Assert.NotNull(BinaryMaskDecoder.Decode(ok, Spec()));

        var v2 = (byte[])ok.Clone();
        v2[2] = 2;                                       // version = 2
        Assert.Null(BinaryMaskDecoder.Decode(v2, Spec()));
    }

    [Fact]
    public void Magic不符的封包要丟掉()
    {
        var bad = Frame(MsgJoystick, 1, 0, 2, 2, new byte[18]);
        bad[0] = 0x58;
        Assert.Null(BinaryMaskDecoder.Decode(bad, Spec()));
    }

    [Fact]
    public void 標頭欄位都解得出來()
    {
        var kv = Decode(Frame(MsgJoystick, 4242, 1_700_000_000_123UL, 2, 2, new byte[18]));

        Assert.Equal("4242", kv["seq"]);
        Assert.Equal("1700000000123", kv["ts"]);
        Assert.Equal("2", kv["conn"]);        // 2 = Connected
        Assert.Equal("2", kv["units"]);       // unitCount:先前完全沒解
    }

    // ── §5.1 msgType=1 搖桿(封包總長 37) ──────────────────────────────────

    [Fact]
    public void 搖桿封包的軸值與狀態位元()
    {
        var payload = new List<byte>();
        payload.AddRange(F32(-0.5f));                    // 左 x
        payload.AddRange(F32(0.25f));                    // 左 y
        payload.Add(0b1101);                             // bit0 X冗餘 bit2 Stale bit3 Raw
        payload.AddRange(F32(1.0f));                     // 右 x
        payload.AddRange(F32(-1.0f));                    // 右 y
        payload.Add(0b0000);

        var frame = Frame(MsgJoystick, 1, 0, 2, 2, payload.ToArray());
        Assert.Equal(37, frame.Length);                  // 規格:19+9+9

        var kv = Decode(frame);
        Assert.Equal("-0.5",  kv["jlx"]);
        Assert.Equal("0.25",  kv["jly"]);
        Assert.Equal("1",     kv["jlf"]);                // bitrange bit0..1 → 1 = 只有 X 冗餘故障
        Assert.Equal("1",     kv["jlst"]);
        Assert.Equal("1",     kv["jlraw"]);
        Assert.Equal("1",     kv["jrx"]);
        Assert.Equal("-1",    kv["jry"]);
        Assert.Equal("0",     kv["jrf"]);
        Assert.Equal("0",     kv["jrst"]);
    }

    // ── §5.2 msgType=2 按鈕 / 急停 / 踏板(V1.1 的變更,封包總長 23) ────────

    /// <summary>
    /// V1.1 的重點。規格 §5.2:bit0..2 = BTN1..3(1=按下),
    /// **bit3 = 左搖桿急停、bit4 = 右搖桿急停,兩者 1=鬆開、0=按下**,
    /// bit5 = 踏板(1=踩下)。
    ///
    /// 急停是反相的,所以 mask 用查表把它正規化成 ESTOP / OK ——
    /// 把反相邏輯留給每個下游各自處理,漏一個就是安全事故。
    /// </summary>
    [Fact]
    public void 按鈕封包_一切正常時急停為OK()
    {
        // BTN2 按下、兩個急停鬆開(bit3=bit4=1)、踏板沒踩
        byte left  = 0b0001_1010;
        byte right = 0b0001_1010;
        var frame = Frame(MsgButtons, 7, 0, 2, 2, left, 0, right, 0);
        Assert.Equal(23, frame.Length);                  // 規格:19+2+2

        var kv = Decode(frame);
        Assert.Equal("0", kv["bl1"]);
        Assert.Equal("1", kv["bl2"]);
        Assert.Equal("0", kv["bl3"]);
        Assert.Equal("OK", kv["estopl"]);
        Assert.Equal("OK", kv["estopr"]);
        Assert.Equal("0",  kv["pedal"]);
    }

    [Fact]
    public void 按鈕封包_左急停按下時解出ESTOP()
    {
        // bit3 = 0 → 左急停按下;bit4 = 1 → 右急停仍鬆開
        byte left  = 0b0001_0000;
        byte right = 0b0001_0000;
        var kv = Decode(Frame(MsgButtons, 8, 0, 2, 2, left, 0, right, 0));

        Assert.Equal("ESTOP", kv["estopl"]);
        Assert.Equal("OK",    kv["estopr"]);
    }

    /// <summary>
    /// 這條釘住 V1.1 之前的行為為什麼危險:舊模擬器 bit3..7 一律送 0,
    /// 在 V1.1 的讀法下等於**兩個急停都被按下**。mask 現在會如實解出來,
    /// 下游就不會把「沒實作新欄位」誤當成「一切正常」。
    /// </summary>
    [Fact]
    public void 按鈕封包_舊版位元組會被解讀成兩個急停都按下()
    {
        byte legacy = 0b0000_0001;                       // 只有 BTN1,高位全 0
        var kv = Decode(Frame(MsgButtons, 9, 0, 2, 2, legacy, 0, legacy, 0));

        Assert.Equal("ESTOP", kv["estopl"]);
        Assert.Equal("ESTOP", kv["estopr"]);
    }

    [Fact]
    public void 按鈕封包_踏板踩下()
    {
        byte v = 0b0011_1000;                            // 急停鬆開 + 踏板踩下
        var kv = Decode(Frame(MsgButtons, 10, 0, 2, 2, v, 0, v, 0));

        Assert.Equal("1",  kv["pedal"]);
        Assert.Equal("OK", kv["estopl"]);
        Assert.Equal("OK", kv["estopr"]);
    }

    /// <summary>規格:左右兩支搖桿封包都包含急停與踏板 —— 右 slot 解出來可交叉驗證。</summary>
    [Fact]
    public void 按鈕封包_右slot帶同一份急停與踏板()
    {
        byte v = 0b0011_0000;                            // bit3=0 左急停按下、bit4=1 右鬆開、bit5=1 踏板踩下
        var kv = Decode(Frame(MsgButtons, 11, 0, 2, 2, v, 0, v, 0));

        Assert.Equal(kv["estopl"], kv["estopl2"]);
        Assert.Equal(kv["estopr"], kv["estopr2"]);
        Assert.Equal(kv["pedal"],  kv["pedal2"]);
        Assert.Equal("ESTOP", kv["estopl2"]);
    }

    [Fact]
    public void 按鈕封包_stale位元()
    {
        var kv = Decode(Frame(MsgButtons, 12, 0, 2, 2, 0b0001_1000, 0b100, 0b0001_1000, 0));
        Assert.Equal("1", kv["blst"]);
        Assert.Equal("0", kv["brst"]);
    }

    // ── §5.3 msgType=3 編碼器(封包總長 31) ────────────────────────────────

    [Fact]
    public void 編碼器封包的位置角度與狀態()
    {
        var payload = new List<byte> { 128, 0b01 };      // position 128、GrayMismatch
        payload.AddRange(F32(180.0f));
        payload.Add(64);
        payload.Add(0b10);                               // Stale
        payload.AddRange(F32(90.0f));

        var frame = Frame(MsgEncoder, 3, 0, 2, 2, payload.ToArray());
        Assert.Equal(31, frame.Length);                  // 規格:19+6+6

        var kv = Decode(frame);
        Assert.Equal("128", kv["e1p"]);
        Assert.Equal("180", kv["e1deg"]);
        Assert.Equal("1",   kv["e1gm"]);
        Assert.Equal("0",   kv["e1st"]);
        Assert.Equal("64",  kv["e2p"]);
        Assert.Equal("90",  kv["e2deg"]);
        Assert.Equal("0",   kv["e2gm"]);
        Assert.Equal("1",   kv["e2st"]);
    }

    // ── §5.4 connState 對照 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]     // Disconnected
    [InlineData(1)]     // Connecting
    [InlineData(2)]     // Connected
    [InlineData(3)]     // Reconnecting
    [InlineData(255)]   // NotPresent
    public void connState五個值都解得出來(byte conn)
    {
        var kv = Decode(Frame(MsgEncoder, 1, 0, conn, conn == 255 ? (byte)0 : (byte)2, new byte[12]));
        Assert.Equal(conn.ToString(), kv["conn"]);
    }

    [Fact]
    public void 長度不符的封包要丟掉()
    {
        var shortFrame = new byte[30];
        Frame(MsgEncoder, 1, 0, 2, 2, new byte[12]).AsSpan(0, 30).CopyTo(shortFrame);
        Assert.Null(BinaryMaskDecoder.Decode(shortFrame, Spec()));
    }
}
