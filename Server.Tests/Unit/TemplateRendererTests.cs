using System.Collections.Generic;
using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>
/// 樣板算繪改成「編譯一次 + 快取」之後,語意必須與原本的兩次 regex pass 完全一致。
/// 這條路徑同時服務所有純文字 mask,行為走鐘的話影響範圍是全部的埠。
/// </summary>
public class TemplateRendererTests
{
    private static MaskDefinition Def(string template) => new()
    {
        maskId = "T", fieldDelimiter = ";", kvSeparator = ":", outputTemplate = template,
    };

    private static string Run(MaskDefinition def, string input) =>
        MaskProcessor.Process(def, [], input);

    [Fact]
    public void Render_SubstitutesAllPlaceholders()
    {
        Assert.Equal("a=1,b=2", Run(Def("a={x},b={y}"), "x:1;y:2"));
    }

    /// <summary>樣板裡任一欄位缺漏 → 整筆丟棄(回 ""),不是留下未替換的 {x}。</summary>
    [Fact]
    public void Render_DropsWhenAnyPlaceholderMissing()
    {
        Assert.Equal("", Run(Def("a={x},b={y}"), "x:1"));
        Assert.Equal("", Run(Def("{missing}"), "x:1"));
    }

    [Fact]
    public void Render_KeepsLiteralTextIncludingEdges()
    {
        Assert.Equal("<1>",     Run(Def("<{x}>"), "x:1"));
        Assert.Equal("1",       Run(Def("{x}"), "x:1"));
        Assert.Equal("11",      Run(Def("{x}{x}"), "x:1"));       // 同一欄位重複出現
        Assert.Equal("nofield", Run(Def("nofield"), "x:1"));       // 完全沒有 placeholder
    }

    /// <summary>
    /// 大括號的匹配規則要與原本的 regex(<c>\{([^{}]+)\}</c>)一致,包含兩個反直覺的情況:
    /// 空的 <c>{}</c> 不算 placeholder(至少要一個字元);而 <c>{a{b}</c> 裡的 <c>{b}</c>
    /// **會**被匹配到 —— 前面多打的那個左括號只是字面文字。
    /// </summary>
    [Fact]
    public void Render_MatchesBracesExactlyLikeTheOriginalRegex()
    {
        Assert.Equal("{}1", Run(Def("{}{x}"), "x:1"));
        Assert.Equal("{a2", Run(Def("{a{b}"), "x:1;b:2"));
        Assert.Equal("",    Run(Def("{a{b}"), "x:1"));        // b 缺 → 整筆丟棄
    }

    /// <summary>
    /// 快取是以「擁有樣板的物件」為 key。同一個物件被就地改寫樣板時必須重編 ——
    /// 否則使用者在 WebUI 改了樣板卻看到舊的輸出,而且完全沒有徵兆。
    /// </summary>
    [Fact]
    public void Render_RecompilesWhenTemplateMutatedInPlace()
    {
        var def = Def("a={x}");
        Assert.Equal("a=1", Run(def, "x:1;y:2"));

        def.outputTemplate = "b={y}";
        Assert.Equal("b=2", Run(def, "x:1;y:2"));

        def.outputTemplate = "";
        Assert.Equal("x:1;y:2", Run(def, "x:1;y:2"));   // 空樣板 = 原樣輸出(既有行為)
    }

    /// <summary>二進位解碼的樣板走同一套,快取 key 是 variant。</summary>
    [Fact]
    public void Render_WorksPerVariantForBinaryMasks()
    {
        var spec = new BinarySpec
        {
            byteOrder = "little",
            discriminator = new BinaryFieldRef { offset = 0, type = "u8" },
            variants =
            [
                new BinaryVariant { match = 1, length = 3, template = "one:{a}",
                    fields = [ new(){ name="a", offset=1, type="u8" } ] },
                new BinaryVariant { match = 2, length = 3, template = "two:{a};{b}",
                    fields = [ new(){ name="a", offset=1, type="u8" }, new(){ name="b", offset=2, type="u8" } ] },
            ]
        };

        Assert.Equal("one:7",    BinaryMaskDecoder.Decode(new byte[] { 1, 7, 0 }, spec));
        Assert.Equal("two:7;9",  BinaryMaskDecoder.Decode(new byte[] { 2, 7, 9 }, spec));
        Assert.Equal("one:7",    BinaryMaskDecoder.Decode(new byte[] { 1, 7, 0 }, spec));   // 快取重用
    }
}
