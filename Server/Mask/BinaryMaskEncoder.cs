using System.Buffers.Binary;
using System.Globalization;

namespace EdgeLink.Mask;

/// <summary>
/// <see cref="BinaryMaskDecoder"/> 的鏡像:把 KV 欄位依 <see cref="BinarySpec"/> 填成固定版面的
/// 二進位封包。用在「EdgeLink 要對外送二進位」的方向(下游講 KV、對端只吃二進位)。
///
/// 與解碼的不對稱處:
///   • sync(magic/version)與 discriminator 由編碼器自己寫,不必宣告成欄位 —— 這兩者的值
///     是版面本身決定的,讓使用者從 KV 傳進來只會多一個填錯的機會。
///   • <see cref="BinaryField.auto"/> 的欄位由這裡產生(seq / 時間戳 / 封包長度),不看 KV。
///   • 沒被宣告的位元組維持 0,正好對應協定文件裡「保留欄位填 0 即可」。
///
/// 回傳 null = 這筆不送(對不到 variant、variant 沒有固定長度、必填欄位缺漏或值不合法)。
/// 「對不到 variant」包含 discriminator 有值但不等於任何 variant 的 match —— 編碼方向
/// 不會退回 default variant,那只是解碼方向「看不懂也盡量解」的策略。
/// 缺欄位預設整包丟棄而不是靜默填 0:送出一筆被歸零的運動命令,遠比沒送出去危險。
/// 真的允許留白的欄位請標 <see cref="BinaryField.optional"/>。
/// </summary>
public static class BinaryMaskEncoder
{
    public static byte[]? Encode(IReadOnlyDictionary<string, string> fields, BinarySpec spec,
        BinarySeqCounters? seq = null)
    {
        var variant = SelectVariant(fields, spec);
        if (variant == null || variant.length <= 0) return null;

        bool big = spec.byteOrder.Equals("big", StringComparison.OrdinalIgnoreCase);
        var buf = new byte[variant.length];

        // 1) sync magic(含 version)—— 版面固定的前綴
        var sync = ParseHex(spec.sync);
        if (sync.Length > buf.Length) return null;
        sync.CopyTo(buf.AsSpan());

        // 2) discriminator —— 值就是 variant.match,不從 KV 取
        if (spec.discriminator != null &&
            !WriteInteger(buf, spec.discriminator.offset, spec.discriminator.type, variant.match, big))
            return null;

        // 3) 一般欄位
        foreach (var f in variant.fields)
        {
            string t = (f.type ?? "").ToLowerInvariant();
            if (t == "const") continue;                 // 解碼專用的常數輸出,沒有對應的位元組

            // 查表欄位是解碼方向的衍生輸出,反查不回去(協定文件對驅動器異警表明講「不可反查」)。
            // 一律跳過,不當成必填欄位。沒有伴生原始欄位的話那幾個位元組就留 0 ——
            // 這是刻意的,對應檔頭那句「沒被宣告的位元組維持 0」:同一份 spec 常常
            // 只有入站方向會用到那些查表欄位,出站方向根本不產生那段內容。
            if (!string.IsNullOrEmpty(f.mapRef)) continue;

            if (!string.IsNullOrEmpty(f.auto))
            {
                if (!WriteAuto(buf, f, t, variant, seq, big)) return null;
                continue;
            }

            if (!fields.TryGetValue(f.name, out var raw))
            {
                if (f.optional) continue;               // 留 0
                return null;
            }
            if (!WriteValue(buf, f, t, raw, big)) return null;
        }

        return buf;
    }

    /// <summary>依 KV 裡的 discriminator 欄位挑 variant。對不到任何 variant 就回 null(不送)。</summary>
    private static BinaryVariant? SelectVariant(IReadOnlyDictionary<string, string> fields, BinarySpec spec)
    {
        if (spec.discriminator == null)
            return spec.variants.FirstOrDefault(v => v.isDefault) ?? spec.variants.FirstOrDefault();

        foreach (var v in spec.variants)
        {
            if (v.isDefault) continue;

            // discriminator 只有 offset/type,沒有名字 —— 借該 variant 宣告在同一個位址的欄位取名
            var keyField = v.fields.FirstOrDefault(f => f.offset == spec.discriminator.offset &&
                !"const".Equals(f.type, StringComparison.OrdinalIgnoreCase));
            if (keyField == null) continue;

            if (fields.TryGetValue(keyField.name, out var raw) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val) &&
                val == v.match)
                return v;
        }

        // 對不到就不送。編碼方向**不能**退回 default variant:呼叫端指名了一個
        // msgType,我們產不出來,那就不該送。退回 default 會送出一筆「長度與版面
        // 屬於別種訊息、msgType 欄位卻是呼叫端填的那個值」的封包(default variant
        // 若宣告了同位址的欄位,它會把 discriminator 覆蓋掉),對端一定誤讀 ——
        // 而且沒有任何錯誤訊號。解碼方向退回 default 是對的,那是「看不懂的封包
        // 也盡量解」;編碼方向沒有這種寬容空間。
        return null;
    }

    private static bool WriteAuto(byte[] buf, BinaryField f, string type, BinaryVariant variant,
        BinarySeqCounters? seq, bool big)
    {
        long value = f.auto.ToLowerInvariant() switch
        {
            // seq 沒有 counter 可用時(預覽、單元測試)給 1 而不是 0 —— 0 在協定上代表「從未發送」
            "seq"         => seq?.Next(variant.match) ?? 1u,
            "timems"      => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "framelength" => variant.length,
            _             => long.MinValue,
        };
        if (value == long.MinValue) return false;       // 不認得的 auto 種類

        // seq 與 timeMs 是「只會一直長大」的量,塞不進欄位寬度時必須回捲而不是丟包:
        // 循環序號的協定語意本來就是 mod 2^n,而毫秒時間戳的常見用法是取低位。
        // 先前這兩者也走 WriteInteger 的 FitsIn 檢查,後果是 u16 的 seq 一過 65535、
        // u32 的 timeMs 從第一筆開始,就每一筆都被丟掉,而且**永遠不會恢復**
        // (counter 與時間只會繼續往上),呼叫端只看到 Encode 一直回 null。
        //
        // frameLength 不回捲:長度塞不進宣告的型別是版面本身寫錯,該讓它失敗。
        if (f.auto.Equals("seq", StringComparison.OrdinalIgnoreCase) ||
            f.auto.Equals("timeMs", StringComparison.OrdinalIgnoreCase))
            value = WrapToType(type, value);

        return WriteInteger(buf, f.offset, type, value, big);
    }

    private static bool WriteValue(byte[] buf, BinaryField f, string type, string raw, bool big)
    {
        if (type == "bit")
        {
            bool on;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bv)) on = bv != 0;
            else if (bool.TryParse(raw, out bool fb)) on = fb;   // 容忍下游送 "true"/"false"
            else return false;

            long byteIdx = (long)f.offset + f.bit / 8;
            if (f.offset < 0 || f.bit < 0 || byteIdx >= buf.Length) return false;
            if (on) buf[(int)byteIdx] |= (byte)(1 << (f.bit % 8));
            else    buf[(int)byteIdx] &= (byte)~(1 << (f.bit % 8));
            return true;
        }

        if (type == "bitrange")
        {
            if (f.offset < 0 || f.bit < 0 || f.count < 1 || f.count > 32) return false;
            if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint val)) return false;
            int need = (f.bit + f.count + 7) / 8;
            if ((long)f.offset + need > buf.Length) return false;

            for (int i = 0; i < f.count; i++)
            {
                int abs = f.bit + i;
                int idx = f.offset + abs / 8;
                if (((val >> i) & 1) != 0) buf[idx] |= (byte)(1 << (abs % 8));
                else                       buf[idx] &= (byte)~(1 << (abs % 8));
            }
            return true;
        }

        // 整數且沒有 scale/add → 走精確整數路徑,不經 double
        // (u64 超過 2^53 用 double 會被靜默改值 —— 時間戳、計數器正是這種欄位)
        if (f.scale == 1.0 && f.add == 0.0 && type is not ("f32" or "f64"))
        {
            if (type == "u64")
            {
                if (!ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u)) return false;
                return WriteUInt64(buf, f.offset, u, big);
            }
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return WriteInteger(buf, f.offset, type, l, big);
            // 允許 "1.0" 這種寫法落到 double 路徑
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
        if (f.scale == 0.0) return false;                       // 無法反推
        double v = (d - f.add) / f.scale;                       // 解碼是 raw*scale + add,這裡反過來
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;

        if (type == "f32")
        {
            float fv = (float)v;
            if (!float.IsFinite(fv)) return false;              // 超出 f32 可表示範圍 → 會變成 Infinity
            return WriteFloat(buf, f.offset, fv, big);
        }
        if (type == "f64") return WriteDouble(buf, f.offset, v, big);

        if (v < long.MinValue || v > long.MaxValue) return false;
        return WriteInteger(buf, f.offset, type, (long)Math.Round(v, MidpointRounding.AwayFromZero), big);
    }

    private static bool WriteInteger(byte[] buf, int off, string type, long value, bool big)
    {
        string t = (type ?? "").ToLowerInvariant();
        int size = BinaryMaskDecoder.SizeOfType(t);
        if (size == 0 || off < 0 || off > buf.Length - size) return false;
        // 塞不進目標型別就整包丟棄,不做無聲截斷。解碼那側越界是回 null(丟包),
        // 編碼這側若截斷則會「送出一筆數值錯誤的命令」—— 那比不送出去嚴重得多。
        if (!FitsIn(t, value)) return false;
        var s = buf.AsSpan(off, size);

        switch (t)
        {
            case "u8":  s[0] = unchecked((byte)value);  return true;
            case "i8":  s[0] = unchecked((byte)(sbyte)value); return true;
            case "u16": if (big) BinaryPrimitives.WriteUInt16BigEndian(s, unchecked((ushort)value));
                        else     BinaryPrimitives.WriteUInt16LittleEndian(s, unchecked((ushort)value)); return true;
            case "i16": if (big) BinaryPrimitives.WriteInt16BigEndian(s, unchecked((short)value));
                        else     BinaryPrimitives.WriteInt16LittleEndian(s, unchecked((short)value)); return true;
            case "u32": if (big) BinaryPrimitives.WriteUInt32BigEndian(s, unchecked((uint)value));
                        else     BinaryPrimitives.WriteUInt32LittleEndian(s, unchecked((uint)value)); return true;
            case "i32": if (big) BinaryPrimitives.WriteInt32BigEndian(s, unchecked((int)value));
                        else     BinaryPrimitives.WriteInt32LittleEndian(s, unchecked((int)value)); return true;
            case "u64": return WriteUInt64(buf, off, unchecked((ulong)value), big);
            case "i64": if (big) BinaryPrimitives.WriteInt64BigEndian(s, value);
                        else     BinaryPrimitives.WriteInt64LittleEndian(s, value); return true;
            case "f32": return WriteFloat(buf, off, value, big);
            case "f64": return WriteDouble(buf, off, value, big);
            default:    return false;
        }
    }

    /// <summary>把值截到目標型別的寬度(捨去高位)。只給會回捲的 auto 欄位用。</summary>
    private static long WrapToType(string type, long v) => type switch
    {
        "u8"  => unchecked((byte)v),
        "i8"  => unchecked((sbyte)v),
        "u16" => unchecked((ushort)v),
        "i16" => unchecked((short)v),
        "u32" => unchecked((uint)v),
        "i32" => unchecked((int)v),
        _     => v,      // u64 / i64 / 浮點:long 一定塞得下
    };

    private static bool FitsIn(string type, long v) => type switch
    {
        "u8"  => v is >= byte.MinValue  and <= byte.MaxValue,
        "i8"  => v is >= sbyte.MinValue and <= sbyte.MaxValue,
        "u16" => v is >= ushort.MinValue and <= ushort.MaxValue,
        "i16" => v is >= short.MinValue  and <= short.MaxValue,
        "u32" => v is >= 0 and <= uint.MaxValue,
        "i32" => v is >= int.MinValue and <= int.MaxValue,
        "u64" => v >= 0,
        _     => true,   // i64 / f32 / f64:long 一定塞得下
    };

    private static bool WriteUInt64(byte[] buf, int off, ulong value, bool big)
    {
        if (off < 0 || off > buf.Length - 8) return false;
        var s = buf.AsSpan(off, 8);
        if (big) BinaryPrimitives.WriteUInt64BigEndian(s, value);
        else     BinaryPrimitives.WriteUInt64LittleEndian(s, value);
        return true;
    }

    private static bool WriteFloat(byte[] buf, int off, float value, bool big)
    {
        if (off < 0 || off > buf.Length - 4) return false;
        var s = buf.AsSpan(off, 4);
        if (big) BinaryPrimitives.WriteSingleBigEndian(s, value);
        else     BinaryPrimitives.WriteSingleLittleEndian(s, value);
        return true;
    }

    private static bool WriteDouble(byte[] buf, int off, double value, bool big)
    {
        if (off < 0 || off > buf.Length - 8) return false;
        var s = buf.AsSpan(off, 8);
        if (big) BinaryPrimitives.WriteDoubleBigEndian(s, value);
        else     BinaryPrimitives.WriteDoubleLittleEndian(s, value);
        return true;
    }

    private static byte[] ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return [];
        hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
        if (hex.Length == 0 || hex.Length % 2 != 0) return [];
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]))
                return [];
        return b;
    }
}
