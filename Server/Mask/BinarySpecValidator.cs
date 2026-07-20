using System.Globalization;

namespace EdgeLink.Mask;

/// <summary>
/// 在「存檔當下」驗證 BinarySpec,而不是等到收包時才炸。
///
/// 先前完全不驗證,於是離譜的 offset 或整數專用的格式字串(例如想 dump 十六進位
/// 而填了 "X2")會在解碼時拋例外。雖然接收迴圈現在有 try/catch 不會整個停擺,
/// 但使用者只會看到每包一行錯誤、卻不知道是自己設定寫錯 —— 在存檔時擋下才有意義。
/// </summary>
public static class BinarySpecValidator
{
    private const int MaxBitCount = 32;

    /// <summary>驗證通過回 null,否則回一句可直接顯示給使用者的錯誤訊息。</summary>
    public static string? Validate(BinarySpec? spec)
    {
        if (spec == null) return null;   // 純文字 mask,不需要驗證

        if (!spec.byteOrder.Equals("little", StringComparison.OrdinalIgnoreCase) &&
            !spec.byteOrder.Equals("big", StringComparison.OrdinalIgnoreCase))
            return $"byteOrder 只能是 little 或 big(收到 '{spec.byteOrder}')";

        if (spec.sync.Length > 0 && ParseHexLength(spec.sync) == 0)
            return $"sync 必須是偶數長度的 hex 字串,例如 \"4f4b\"(收到 '{spec.sync}')";

        if (spec.discriminator != null)
        {
            var d = spec.discriminator;
            if (d.offset < 0) return "discriminator.offset 不可為負數";
            if (BinaryMaskDecoder.SizeOfType(d.type) == 0 ||
                d.type.Equals("f32", StringComparison.OrdinalIgnoreCase) ||
                d.type.Equals("f64", StringComparison.OrdinalIgnoreCase))
                return $"discriminator.type 必須是整數型別(u8/i8/u16/i16/u32/i32/u64/i64),收到 '{d.type}'";
        }

        if (spec.variants.Count == 0) return "至少要有一個 variant";

        for (int i = 0; i < spec.variants.Count; i++)
        {
            var v = spec.variants[i];
            string where = $"variant[{i}]" + (v.isDefault ? "(default)" : $"(match={v.match})");

            if (v.length < 0) return $"{where} 的 length 不可為負數";

            // TCP 分包需要靠 length 決定要收多少位元組;length=0 在 TCP 上永遠框不出封包。
            if (v.length == 0 && spec.sync.Length > 0)
                return $"{where} 的 length 為 0 —— 有設 sync(TCP 分包)時每個 variant 都必須指定長度";

            if (string.IsNullOrEmpty(v.template))
                return $"{where} 缺少 template";

            foreach (var f in v.fields)
            {
                string? err = ValidateField(f, v.length, where);
                if (err != null) return err;
            }
        }

        return null;
    }

    private static string? ValidateField(BinaryField f, int variantLength, string where)
    {
        string at = $"{where} 的欄位 '{f.name}'";
        if (string.IsNullOrWhiteSpace(f.name)) return $"{where} 有欄位沒有名稱";

        string t = (f.type ?? "").ToLowerInvariant();

        if (t == "const") return null;   // 不讀封包

        if (f.offset < 0) return $"{at} 的 offset 不可為負數";

        if (t == "bit" || t == "bitrange")
        {
            if (f.bit < 0) return $"{at} 的 bit 不可為負數";
            int count = t == "bit" ? 1 : f.count;
            if (count < 1 || count > MaxBitCount)
                return $"{at} 的 count 必須介於 1 到 {MaxBitCount}(收到 {f.count})";

            int needBytes = (f.bit + count + 7) / 8;
            if (variantLength > 0 && (long)f.offset + needBytes > variantLength)
                return $"{at} 超出封包長度({f.offset} + {needBytes} 位元組 > length {variantLength})";
            return null;
        }

        int size = BinaryMaskDecoder.SizeOfType(t);
        if (size == 0) return $"{at} 的 type '{f.type}' 不認得";

        if (variantLength > 0 && (long)f.offset + size > variantLength)
            return $"{at} 超出封包長度({f.offset} + {size} 位元組 > length {variantLength})";

        // format 是使用者自由輸入,先試跑一次以免存進去後每包都拋 FormatException
        if (!string.IsNullOrEmpty(f.format))
        {
            try { _ = 1.0.ToString(f.format, CultureInfo.InvariantCulture); }
            catch (FormatException)
            {
                return $"{at} 的 format '{f.format}' 不是合法的數值格式" +
                       "(整數專用的格式如 X2/D 不適用,想輸出十六進位請改用其他方式)";
            }
        }

        return null;
    }

    /// <summary>回傳 hex 字串代表的位元組數;格式不合法回 0。</summary>
    private static int ParseHexLength(string hex)
    {
        hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
        if (hex.Length == 0 || hex.Length % 2 != 0) return 0;
        for (int i = 0; i < hex.Length; i++)
            if (!Uri.IsHexDigit(hex[i])) return 0;
        return hex.Length / 2;
    }
}
