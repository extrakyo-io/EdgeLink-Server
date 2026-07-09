using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EdgeLink.Mask;

// 把原始二進位封包依 BinarySpec 解成命名欄位,再套 variant.template 產出 KV 文字。
// 回傳 null / "" = 該封包被丟棄(discriminator 無對應 variant、長度不符、或 template 有缺欄位)。
public static class BinaryMaskDecoder
{
    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    public static string? Decode(ReadOnlySpan<byte> data, BinarySpec spec)
    {
        var variant = SelectVariant(data, spec);
        if (variant == null) return null;
        if (variant.length > 0 && data.Length != variant.length) return null;

        bool big = spec.byteOrder.Equals("big", StringComparison.OrdinalIgnoreCase);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in variant.fields)
        {
            string? v = ReadField(data, f, big);
            if (v != null) fields[f.name] = v;
        }
        return ApplyTemplate(variant.template, fields);
    }

    private static BinaryVariant? SelectVariant(ReadOnlySpan<byte> data, BinarySpec spec)
    {
        if (spec.discriminator == null)
            return spec.variants.FirstOrDefault(v => v.isDefault) ?? spec.variants.FirstOrDefault();

        long? disc = ReadInt(data, spec.discriminator.offset, spec.discriminator.type,
            spec.byteOrder.Equals("big", StringComparison.OrdinalIgnoreCase));
        if (disc == null)
            return spec.variants.FirstOrDefault(v => v.isDefault);

        return spec.variants.FirstOrDefault(v => !v.isDefault && v.match == disc.Value)
            ?? spec.variants.FirstOrDefault(v => v.isDefault);
    }

    private static string? ReadField(ReadOnlySpan<byte> d, BinaryField f, bool big)
    {
        string t = f.type.ToLowerInvariant();
        if (t == "const") return f.value;

        if (t == "bit")
        {
            if (f.offset < 0 || f.offset >= d.Length) return null;
            return ((d[f.offset] >> f.bit) & 1) != 0 ? "1" : "0";
        }
        if (t == "bitrange")
        {
            if (f.offset < 0 || f.offset >= d.Length) return null;
            int mask = (1 << Math.Clamp(f.count, 1, 8)) - 1;
            int val = (d[f.offset] >> f.bit) & mask;
            return val.ToString(CultureInfo.InvariantCulture);
        }

        // 數值型
        double? raw = ReadNumber(d, f.offset, t, big);
        if (raw == null) return null;
        double outv = raw.Value * f.scale + f.add;

        bool isFloat = t is "f32" or "f64" || f.scale != 1.0 || f.add != 0.0;
        string fmt = string.IsNullOrEmpty(f.format) ? (isFloat ? "0.######" : "0") : f.format;
        return outv.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static long? ReadInt(ReadOnlySpan<byte> d, int off, string type, bool big)
    {
        double? n = ReadNumber(d, off, type.ToLowerInvariant(), big);
        return n == null ? null : (long)n.Value;
    }

    private static double? ReadNumber(ReadOnlySpan<byte> d, int off, string t, bool big)
    {
        int size = t switch
        {
            "u8" or "i8" => 1,
            "u16" or "i16" => 2,
            "u32" or "i32" or "f32" => 4,
            "u64" or "i64" or "f64" => 8,
            _ => 0
        };
        if (size == 0 || off < 0 || off + size > d.Length) return null;
        var s = d.Slice(off, size);

        switch (t)
        {
            case "u8":  return s[0];
            case "i8":  return (sbyte)s[0];
            case "u16": return big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
            case "i16": return big ? BinaryPrimitives.ReadInt16BigEndian(s)  : BinaryPrimitives.ReadInt16LittleEndian(s);
            case "u32": return big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
            case "i32": return big ? BinaryPrimitives.ReadInt32BigEndian(s)  : BinaryPrimitives.ReadInt32LittleEndian(s);
            case "u64": return big ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);
            case "i64": return big ? BinaryPrimitives.ReadInt64BigEndian(s)  : BinaryPrimitives.ReadInt64LittleEndian(s);
            case "f32": return big ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
            case "f64": return big ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s);
            default: return null;
        }
    }

    // 與 MaskProcessor 相同語意:樣板中任一 {欄位} 缺 → 回 ""(丟棄)
    private static string ApplyTemplate(string template, Dictionary<string, string> fields)
    {
        if (string.IsNullOrEmpty(template)) return "";
        foreach (Match m in Placeholder.Matches(template))
            if (!fields.ContainsKey(m.Groups[1].Value)) return "";
        return Placeholder.Replace(template, m => fields.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
