using System.Globalization;
using System.Runtime.CompilerServices;

namespace EdgeLink.Mask;

/// <summary>
/// 具名查表:把 wire 上的錯誤碼換成看得懂的字串(reasonCode、EtherCAT 錯誤、驅動器異警等)。
///
/// 表放在 <see cref="BinarySpec.maps"/>、欄位用 <see cref="BinaryField.mapRef"/> 指名,
/// 而不是每個欄位各自內嵌一份 —— 三支軸共用同一張驅動器異警表,內嵌會變成三份會各自走鐘的副本。
///
/// key 支援十進位("5")、十六進位("0x2310")與範圍("0x0207-0x0249")。查表順序是
/// 先精確、再範圍、最後 <see cref="BinaryField.mapDefault"/>;都對不到就輸出原始數值。
///
/// <para>只作用在解碼方向。編碼時帶 mapRef 的欄位會被跳過 —— 多對一的表本來就反查不回去
/// (協定文件對驅動器異警表明講「不可反查」),而同一個位址上的原始欄位已經寫過那幾個位元組。</para>
/// </summary>
internal static class BinaryValueMaps
{
    // 以 spec 實例為 key:mask 被改寫時會產生新的 BinarySpec,舊的編譯結果跟著被回收
    private static readonly ConditionalWeakTable<BinarySpec, Dictionary<string, ValueMap>> _cache = new();

    /// <summary>查表;找不到表或對不到值時回 null(呼叫端輸出原始數值)。</summary>
    public static string? Lookup(BinarySpec spec, string mapName, long value)
    {
        var compiled = _cache.GetValue(spec, CompileAll);
        return compiled.TryGetValue(mapName, out var map) ? map.Lookup(value) : null;
    }

    /// <summary>驗證所有表的 key 都解得開;通過回 null。</summary>
    public static string? ValidateTables(BinarySpec spec)
    {
        if (spec.maps == null) return null;
        foreach (var (name, table) in spec.maps)
            foreach (var key in table.Keys)
                if (!TryParseKey(key, out _, out _))
                    return $"maps['{name}'] 的 key '{key}' 不是合法的數值或範圍" +
                           "(可用十進位 5、十六進位 0x2310,或範圍 0x0207-0x0249)";
        return null;
    }

    private static Dictionary<string, ValueMap> CompileAll(BinarySpec spec)
    {
        var result = new Dictionary<string, ValueMap>(StringComparer.Ordinal);
        if (spec.maps == null) return result;

        foreach (var (name, table) in spec.maps)
        {
            var map = new ValueMap();
            foreach (var (key, text) in table)
                if (TryParseKey(key, out long lo, out long hi))
                {
                    if (lo == hi) map.Exact[lo] = text;
                    else          map.Ranges.Add((lo, hi, text));
                }
            result[name] = map;
        }
        return result;
    }

    /// <summary>"5" / "0x2310" / "0x0207-0x0249" / "-1" → 數值區間。</summary>
    private static bool TryParseKey(string key, out long lo, out long hi)
    {
        lo = hi = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim();

        // 從第 2 個字元起找 '-' 才算範圍分隔,否則 "-1" 這種負數會被誤判成範圍
        int dash = key.IndexOf('-', 1);
        if (dash > 0)
        {
            if (!TryParseNumber(key[..dash], out lo)) return false;
            if (!TryParseNumber(key[(dash + 1)..], out hi)) return false;
            return lo <= hi;
        }

        if (!TryParseNumber(key, out lo)) return false;
        hi = lo;
        return true;
    }

    private static bool TryParseNumber(string s, out long value)
    {
        s = s.Trim();
        value = 0;
        if (s.Length == 0) return false;

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private sealed class ValueMap
    {
        public readonly Dictionary<long, string> Exact = [];
        public readonly List<(long Lo, long Hi, string Text)> Ranges = [];

        public string? Lookup(long v)
        {
            if (Exact.TryGetValue(v, out var exact)) return exact;
            foreach (var (lo, hi, text) in Ranges)
                if (v >= lo && v <= hi) return text;
            return null;
        }
    }
}
