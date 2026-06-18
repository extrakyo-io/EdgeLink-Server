using EdgeLink.Mask;
using Xunit;

namespace EdgeLink.Tests.Unit;

public class MaskProcessorTests
{
    // ── Null / raw passthrough ────────────────────────────────────────────────

    [Fact]
    public void NullDefinition_ReturnsOriginalText()
    {
        string result = MaskProcessor.Process(null, [], "hello", null);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void RawTemplate_ReturnsOriginalText()
    {
        var def    = Def("{raw}");
        string res = MaskProcessor.Process(def, [], "sensor:temperature;value:36.5", null);
        Assert.Equal("sensor:temperature;value:36.5", res);
    }

    [Fact]
    public void EmptyTemplate_ReturnsOriginalText()
    {
        var def    = Def("");
        string res = MaskProcessor.Process(def, [], "anything", null);
        Assert.Equal("anything", res);
    }

    // ── Field extraction ──────────────────────────────────────────────────────

    [Fact]
    public void SingleField_ExtractedAndApplied()
    {
        var def    = Def("{value}", fieldDelim: ";", kvSep: ":");
        string res = MaskProcessor.Process(def, [], "value:42", null);
        Assert.Equal("42", res);
    }

    [Fact]
    public void MultipleFields_AllSubstituted()
    {
        var def    = Def("{\"id\":\"{id}\",\"temp\":{temp}}", fieldDelim: ";", kvSep: ":");
        string res = MaskProcessor.Process(def, [], "id:DEV01;temp:36.5", null);
        Assert.Equal("{\"id\":\"DEV01\",\"temp\":36.5}", res);
    }

    [Fact]
    public void MissingField_ReturnsEmpty()
    {
        var def    = Def("{missing_field}", fieldDelim: ";", kvSep: ":");
        string res = MaskProcessor.Process(def, [], "id:DEV01;temp:36.5", null);
        Assert.Equal("", res);
    }

    // ── Custom delimiters ─────────────────────────────────────────────────────

    [Fact]
    public void CustomFieldDelimiter_Works()
    {
        var def    = Def("{a},{b}", fieldDelim: "|", kvSep: "=");
        string res = MaskProcessor.Process(def, [], "a=1|b=2", null);
        Assert.Equal("1,2", res);
    }

    [Fact]
    public void CustomKvSeparator_Works()
    {
        var def    = Def("{key}", fieldDelim: ";", kvSep: "=");
        string res = MaskProcessor.Process(def, [], "key=hello", null);
        Assert.Equal("hello", res);
    }

    // ── Extra fields (e.g., correlation ID injection) ─────────────────────────

    [Fact]
    public void ExtraFields_MergedWithExtracted()
    {
        var def    = Def("{id}:{_corrId}", fieldDelim: ";", kvSep: ":");
        var extra  = new Dictionary<string, string> { ["_corrId"] = "XYZ789" };
        string res = MaskProcessor.Process(def, [], "id:DEV01", extra);
        Assert.Equal("DEV01:XYZ789", res);
    }

    [Fact]
    public void ExtraFields_OverrideExtracted()
    {
        var def    = Def("{val}", fieldDelim: ";", kvSep: ":");
        var extra  = new Dictionary<string, string> { ["val"] = "OVERRIDE" };
        string res = MaskProcessor.Process(def, [], "val:original", extra);
        Assert.Equal("OVERRIDE", res);
    }

    // ── Default delimiters when empty string ──────────────────────────────────

    [Fact]
    public void EmptyDelimiter_UsesDefaultSemicolon()
    {
        var def = new MaskDefinition
        {
            maskId        = "test",
            outputTemplate = "{x}",
            fieldDelimiter = "",   // should default to ";"
            kvSeparator    = "",   // should default to ":"
        };
        string res = MaskProcessor.Process(def, [], "x:10;y:20", null);
        Assert.Equal("10", res);
    }

    // ── Empty / whitespace input ──────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var def    = Def("{x}", fieldDelim: ";", kvSep: ":");
        string res = MaskProcessor.Process(def, [], "", null);
        Assert.Equal("", res);
    }

    [Fact]
    public void FieldWithNoKvSeparator_Ignored()
    {
        var def    = Def("{b}", fieldDelim: ";", kvSep: ":");
        // "nokey" has no ":" so it's skipped; "b:2" is parsed
        string res = MaskProcessor.Process(def, [], "nokey;b:2", null);
        Assert.Equal("2", res);
    }

    // ── Multiple placeholders of same field ───────────────────────────────────

    [Fact]
    public void SamePlaceholderTwice_BothReplaced()
    {
        var def    = Def("{id}-{id}", fieldDelim: ";", kvSep: ":");
        string res = MaskProcessor.Process(def, [], "id:ABC", null);
        Assert.Equal("ABC-ABC", res);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static MaskDefinition Def(string template, string fieldDelim = ";", string kvSep = ":") =>
        new()
        {
            maskId         = "test",
            outputTemplate  = template,
            fieldDelimiter  = fieldDelim,
            kvSeparator     = kvSep,
        };
}
