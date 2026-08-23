using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>
/// 稽核指出的三項編碼器疑慮。每一條都先以實際行為重現過,確認是真缺陷之後才修 ——
/// 其中一條的機制與稽核的描述不同(見 <see cref="Discriminator_對不到就不送"/>)。
/// </summary>
public class BinaryMaskEncoderAuditTests
{
    private static Dictionary<string, string> Kv(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ── 1) auto 的 seq / timeMs 必須依欄位寬度回捲,不是丟包 ──────────────────

    /// <summary>
    /// `auto: timeMs` 宣告在 u32 上。Unix 毫秒現在約 1.77e12,遠超過 uint.MaxValue(4.29e9)。
    ///
    /// 修正前:WriteInteger 的 FitsIn 檢查回 false → Encode 回 null → **每一筆都送不出去**,
    /// 而且永遠不會恢復(時間只會越來越大),呼叫端只看到 null,沒有任何錯誤訊息。
    /// 修正後:取低 32 位。毫秒時間戳取低位是協定上的常見做法。
    /// </summary>
    [Fact]
    public void Auto_TimeMs_塞不進u32時取低位而不是丟包()
    {
        var spec = SingleAutoSpec("timeMs", "u32");
        var packet = BinaryMaskEncoder.Encode(Kv(), spec);

        Assert.NotNull(packet);
        uint written = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        uint expected = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // 執行期間時間會走動,只比對高位相近即可(容許幾秒的漂移)
        Assert.True(Math.Abs((long)written - expected) < 10_000,
            $"寫入 {written},預期接近 {expected}");
    }

    /// <summary>
    /// `auto: seq` 宣告在 u16 上。<see cref="BinarySeqCounters.Next"/> 回的是 uint,
    /// 要到 uint.MaxValue 才回捲。
    ///
    /// 修正前:第 65536 筆開始每一筆都塞不進 u16 而被丟掉,之後**永遠**送不出去
    /// (counter 只會繼續往上)。序號欄位在協定上本來就該依欄位寬度回捲。
    /// </summary>
    [Fact]
    public void Auto_Seq_超過u16之後回捲而不是停止發送()
    {
        var spec = SingleAutoSpec("seq", "u16");
        var counters = new BinarySeqCounters();
        for (int i = 0; i < ushort.MaxValue + 1; i++) counters.Next(1);   // counter 推到 65536

        var packet = BinaryMaskEncoder.Encode(Kv(), spec, counters);      // 第 65537 筆

        Assert.NotNull(packet);
        Assert.Equal(65537 & 0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4, 2)));
    }

    /// <summary>frameLength 不回捲 —— 長度塞不進宣告的型別是版面本身寫錯,該讓它失敗。</summary>
    [Fact]
    public void Auto_FrameLength_塞不進型別時仍然丟包()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little",
            sync = "aa55",
            variants =
            [
                new BinaryVariant {
                    match = 1, length = 300,        // 300 塞不進 u8
                    fields = [ new(){ name="len", offset=4, type="u8", auto="frameLength" } ]
                }
            ]
        };

        Assert.Null(BinaryMaskEncoder.Encode(Kv(), spec));
    }

    private static BinarySpec SingleAutoSpec(string auto, string type) => new()
    {
        byteOrder = "little",
        sync = "aa55",
        variants =
        [
            new BinaryVariant {
                match = 1, length = 16,
                fields = [ new(){ name="v", offset=4, type=type, auto=auto } ]
            }
        ]
    };

    // ── 2) discriminator 對不到任何 variant ─────────────────────────────────

    /// <summary>
    /// 稽核說「會退回 default variant 並送出一包 payload 全 0 的封包」。實際重現的結果
    /// 更難查:default variant 若宣告了同位址的欄位,它會把 discriminator **覆蓋成
    /// 呼叫端填的值** —— 送出去的是一筆「msgType 欄位寫著 99、長度與版面卻屬於
    /// default variant」的封包。對端一定誤讀,而且沒有任何錯誤訊號。
    ///
    /// 檔案開頭的合約本來就寫「對不到 variant → 回傳 null」,是程式碼沒照著做。
    /// 編碼方向沒有寬容空間:呼叫端指名了一個 msgType,產不出來就不該送。
    /// </summary>
    [Fact]
    public void Discriminator_對不到就不送()
    {
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "99")), TwoVariantSpec(withDefault: false)));
        Assert.Null(BinaryMaskEncoder.Encode(Kv(("mt", "99")), TwoVariantSpec(withDefault: true)));
    }

    /// <summary>對得到的當然照送 —— 確認上面那條不是把功能整個關掉。</summary>
    [Fact]
    public void Discriminator_對得到時正常送出()
    {
        var packet = BinaryMaskEncoder.Encode(Kv(("mt", "16")), TwoVariantSpec(withDefault: true));

        Assert.NotNull(packet);
        Assert.Equal(16, packet[2]);
    }

    private static BinarySpec TwoVariantSpec(bool withDefault)
    {
        var variants = new List<BinaryVariant>
        {
            new() {
                match = 16, length = 8,
                fields = [ new(){ name="mt", offset=2, type="u8" } ]
            },
        };
        if (withDefault)
            variants.Add(new BinaryVariant {
                match = 0, length = 8, isDefault = true,
                fields = [ new(){ name="mt", offset=2, type="u8" } ]
            });

        return new BinarySpec
        {
            byteOrder = "little",
            sync = "aa55",
            discriminator = new BinaryFieldRef { offset = 2, type = "u8" },
            variants = variants,
        };
    }

    // ── 3) mapRef 欄位在編碼方向被略過 ──────────────────────────────────────

    /// <summary>
    /// mapRef 是解碼方向的衍生輸出(原始碼 → 人看得懂的字串),反查不回去。編碼器刻意
    /// 略過它,靠同一個位址上的原始欄位把位元組寫出去 —— 這是設計決定,不是缺陷。
    /// 這條把那個前提釘住。
    /// </summary>
    [Fact]
    public void MapRef_有伴生原始欄位時照常寫入()
    {
        var packet = BinaryMaskEncoder.Encode(Kv(("code", "5"), ("codeText", "任意值")),
                                              MapRefSpec(withRawCompanion: true));

        Assert.NotNull(packet);
        Assert.Equal(5, packet[2]);
    }

    /// <summary>
    /// 稽核的第三項是**誤報**,而且我照著它改了之後被既有測試擋下來 ——
    /// <c>BinaryValueMapTests.Encode_SkipsMappedFieldsAndStillWritesRaw</c> 用的 spec 裡,
    /// <c>ax</c> / <c>fl</c> / <c>bits</c> 三個 mapRef 欄位本來就沒有伴生原始欄位,
    /// 而那條測試明確斷言這種 spec 必須編得出來。
    ///
    /// 也就是說「mapRef 沒有伴生欄位 → 那幾個位元組留 0」是文件化的設計決定
    /// (檔頭:「沒被宣告的位元組維持 0,正好對應協定文件裡『保留欄位填 0 即可』」),
    /// 不是洞:同一份 spec 常常只有入站方向會用到那些查表欄位,出站方向根本不產生
    /// 那段內容。改成丟包會讓所有這類 spec 完全送不出東西。
    /// </summary>
    [Fact]
    public void MapRef_沒有伴生原始欄位時留0並照常送出()
    {
        var packet = BinaryMaskEncoder.Encode(Kv(("codeText", "任意值")),
                                              MapRefSpec(withRawCompanion: false));

        Assert.NotNull(packet);
        Assert.Equal(0, packet[2]);
    }

    private static BinarySpec MapRefSpec(bool withRawCompanion)
    {
        var fields = new List<BinaryField>();
        if (withRawCompanion) fields.Add(new() { name = "code", offset = 2, type = "u8" });
        fields.Add(new() { name = "codeText", offset = 2, type = "u8", mapRef = "codes" });

        return new BinarySpec
        {
            byteOrder = "little",
            sync = "aa55",
            maps = new Dictionary<string, Dictionary<string, string>>
            {
                ["codes"] = new() { ["5"] = "任意值" },
            },
            variants = [ new BinaryVariant { match = 1, length = 8, fields = fields } ],
        };
    }
}
