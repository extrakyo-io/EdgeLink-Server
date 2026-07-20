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

        // bit:bit 索引可以 >= 8,會自動往後推位元組(offset+bit/8 的第 bit%8 位)。
        // 先前直接 d[offset] >> f.bit,而 C# 對 int 的位移量取模 32,導致
        // bit=12(16 位元狀態字的自然寫法)永遠回 0、bit=32 竟回報 bit 0 的值。
        if (t == "bit")
        {
            if (f.offset < 0 || f.bit < 0) return null;
            long byteIdx = (long)f.offset + f.bit / 8;          // long:避免超大 offset 溢位
            if (byteIdx >= d.Length) return null;
            return ((d[(int)byteIdx] >> (f.bit % 8)) & 1) != 0 ? "1" : "0";
        }

        // bitrange:支援跨位元組。先前 count 被 clamp 到 1..8 且只讀單一位元組,
        // 任何 bit+count > 8 的欄位(例如 12 位元 ADC)都會被靜默截斷成錯誤的值。
        if (t == "bitrange")
        {
            int count = f.count;
            if (f.offset < 0 || f.bit < 0 || count < 1 || count > 32) return null;
            int need = (f.bit + count + 7) / 8;
            if ((long)f.offset + need > d.Length) return null;  // long:避免超大 offset 溢位

            uint val = 0;
            for (int i = 0; i < count; i++)
            {
                int abs = f.bit + i;
                int one = (d[f.offset + abs / 8] >> (abs % 8)) & 1;
                val |= (uint)one << i;
            }
            return val.ToString(CultureInfo.InvariantCulture);
        }

        // 整數型別在沒有 scale/add/自訂格式時走「精確整數」路徑,不經 double。
        // 先前所有數值都轉成 double:u64/i64 超過 2^53 無法精確表示,而自訂數值格式
        // 在 double 上又只保留 15 位有效數字 —— 裝置序號、計數器、ns 時間戳會被靜默算錯
        // (例如 1000000000000001 輸出成 1000000000000000)。
        if (f.scale == 1.0 && f.add == 0.0 && string.IsNullOrEmpty(f.format))
        {
            string? exact = ReadIntegerExact(d, f.offset, t, big);
            if (exact != null) return exact;
        }

        // 浮點 / 有 scale/offset / 有自訂格式 → 走 double
        double? raw = ReadNumber(d, f.offset, t, big);
        if (raw == null) return null;
        double outv = raw.Value * f.scale + f.add;

        bool isFloat = t is "f32" or "f64" || f.scale != 1.0 || f.add != 0.0;
        string fmt = string.IsNullOrEmpty(f.format) ? (isFloat ? "0.######" : "0") : f.format;
        return outv.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>整數型別的精確讀取(不經 double)。非整數型別或越界時回 null,
    /// 呼叫端會落回 double 路徑,行為與先前相容。</summary>
    private static string? ReadIntegerExact(ReadOnlySpan<byte> d, int off, string t, bool big)
    {
        if (off < 0) return null;
        var ci = CultureInfo.InvariantCulture;

        switch (t)
        {
            case "u8":  return off <= d.Length - 1 ? d[off].ToString(ci) : null;
            case "i8":  return off <= d.Length - 1 ? ((sbyte)d[off]).ToString(ci) : null;
            case "u16": return off <= d.Length - 2
                ? (big ? BinaryPrimitives.ReadUInt16BigEndian(d.Slice(off, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(off, 2))).ToString(ci) : null;
            case "i16": return off <= d.Length - 2
                ? (big ? BinaryPrimitives.ReadInt16BigEndian(d.Slice(off, 2)) : BinaryPrimitives.ReadInt16LittleEndian(d.Slice(off, 2))).ToString(ci) : null;
            case "u32": return off <= d.Length - 4
                ? (big ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice(off, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(off, 4))).ToString(ci) : null;
            case "i32": return off <= d.Length - 4
                ? (big ? BinaryPrimitives.ReadInt32BigEndian(d.Slice(off, 4)) : BinaryPrimitives.ReadInt32LittleEndian(d.Slice(off, 4))).ToString(ci) : null;
            case "u64": return off <= d.Length - 8
                ? (big ? BinaryPrimitives.ReadUInt64BigEndian(d.Slice(off, 8)) : BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(off, 8))).ToString(ci) : null;
            case "i64": return off <= d.Length - 8
                ? (big ? BinaryPrimitives.ReadInt64BigEndian(d.Slice(off, 8)) : BinaryPrimitives.ReadInt64LittleEndian(d.Slice(off, 8))).ToString(ci) : null;
            default:    return null;   // f32 / f64 / 未知型別
        }
    }

    /// <summary>某型別佔幾個位元組(未知型別回 0)。BinaryStreamFramer 也用這個決定
    /// 要等多少位元組才讀得到 discriminator。</summary>
    internal static int SizeOfType(string type) => type.ToLowerInvariant() switch
    {
        "u8" or "i8"            => 1,
        "u16" or "i16"          => 2,
        "u32" or "i32" or "f32" => 4,
        "u64" or "i64" or "f64" => 8,
        _                       => 0
    };

    /// <summary>讀整數(直接讀位元組,不經 double,避免大數精度流失)。
    /// 非整數型別或越界回 null。
    /// <para>BinaryStreamFramer 與 SelectVariant 共用這一份 —— 先前 framer 自己有一份
    /// 實作,把 i8 當成無號、把 u64/i64/f32 當成 1 個位元組,導致同一個封包
    /// framer 算出的 discriminator 與 decoder 不一致(例如 i8 的 0xF0:framer 得 240、
    /// decoder 得 -16),framer 因此找不到 variant 而把整包丟掉。</para></summary>
    internal static long? ReadInt(ReadOnlySpan<byte> d, int off, string type, bool big)
    {
        string t = type.ToLowerInvariant();
        int size = SizeOfType(t);
        if (size == 0 || off < 0 || off > d.Length - size) return null;
        var s = d.Slice(off, size);

        return t switch
        {
            "u8"  => (long)s[0],
            "i8"  => (long)(sbyte)s[0],
            "u16" => (long)(big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s)),
            "i16" => (long)(big ? BinaryPrimitives.ReadInt16BigEndian(s)  : BinaryPrimitives.ReadInt16LittleEndian(s)),
            "u32" => (long)(big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s)),
            "i32" => (long)(big ? BinaryPrimitives.ReadInt32BigEndian(s)  : BinaryPrimitives.ReadInt32LittleEndian(s)),
            "i64" => big ? BinaryPrimitives.ReadInt64BigEndian(s)  : BinaryPrimitives.ReadInt64LittleEndian(s),
            "u64" => unchecked((long)(big ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s))),
            _     => (long?)null,   // f32 / f64 不適合當 discriminator
        };
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
        if (size == 0 || off < 0 || off > d.Length - size) return null;
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
