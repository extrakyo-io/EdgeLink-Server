using System.Globalization;

namespace EdgeLink.Mask;

/// <summary>
/// 把 TCP 位元組「流」切成一個個完整的固定版面二進位封包(framing)。
/// UDP 每個 datagram 已是一包、不需要;TCP 是無邊界串流,才需要本類別。
///
/// 分包規則(依 <see cref="BinarySpec"/>):
///   • 對齊:若 spec.sync 有值(如 "4f4b"=magic 'OK'),以它為錨點對齊/重新同步;
///           空字串則假設串流已對齊(TCP 無遺失,來源逐包送即恆對齊),但無法從雜訊中復原。
///   • 長度:讀 discriminator(如 msgType@3)的值 → 對應 variant 的 length。
///   • 殘缺:湊不滿一包就等下一段;對不到 variant 時跳 1 個位元組重新對齊
///           (不會丟棄整個緩衝,以免連後面已完整的封包一起賠掉)。
/// 非執行緒安全:每條連線各自 new 一個。
/// </summary>
public sealed class BinaryStreamFramer
{
    private readonly BinarySpec _spec;
    private readonly byte[]     _sync;
    private readonly bool       _big;

    private byte[] _buf = new byte[4096];
    private int    _len;

    private const int MaxBuffer = 1 << 20;   // 1 MB 失控保護

    public BinaryStreamFramer(BinarySpec spec)
    {
        _spec = spec;
        _big  = spec.byteOrder.Equals("big", StringComparison.OrdinalIgnoreCase);
        _sync = ParseHex(spec.sync);
    }

    /// <summary>把新收到的位元組接進緩衝。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        if (_len + data.Length > MaxBuffer) { _len = 0; return; }   // 失控:清空重來
        if (_len + data.Length > _buf.Length)
        {
            int cap = _buf.Length;
            while (cap < _len + data.Length) cap *= 2;
            Array.Resize(ref _buf, cap);
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    /// <summary>取出下一個完整封包(回傳新陣列);沒有完整封包時回 null。反覆呼叫直到 null。</summary>
    public byte[]? Next()
    {
        while (true)
        {
            // 1) 對齊到 sync magic
            if (_sync.Length > 0)
            {
                int idx = IndexOf(_buf, _len, _sync);
                if (idx < 0)
                {
                    // 找不到:保留末端可能是 sync 前綴的位元組,其餘丟棄
                    int keep = Math.Min(_sync.Length - 1, _len);
                    Shift(_len - keep);
                    return null;
                }
                if (idx > 0) Shift(idx);        // 丟棄對齊前的雜訊
            }

            // 2) 讀 discriminator 決定這一包的長度
            var disc = _spec.discriminator;
            int need = disc != null ? disc.offset + BinaryMaskDecoder.SizeOfType(disc.type) : 1;
            if (need <= 0) return null;         // discriminator 型別不合法 → 無法分包
            if (_len < need) return null;       // 連 discriminator 都還沒收齊

            int length = ResolveLength(disc);
            if (length <= 0)
            {
                // 對不到 variant(或 variant 沒設 length)→ 跳 1 個位元組重新對齊。
                // 先前在沒有 sync 時是 `_len = 0`,會把整個緩衝丟掉 —— 連排在後面
                // 「已經完整」的封包也一起賠進去,而且之後很可能從封包中段開始解讀。
                Shift(1);
                continue;
            }

            if (_len < length) return null;     // 等整包到齊

            var pkt = _buf.AsSpan(0, length).ToArray();
            Shift(length);
            return pkt;
        }
    }

    private int ResolveLength(BinaryFieldRef? disc)
    {
        if (disc == null)
        {
            var def = _spec.variants.FirstOrDefault(v => v.isDefault) ?? _spec.variants.FirstOrDefault();
            return def?.length ?? 0;
        }
        // 與 decoder 共用同一份讀值實作,避免兩邊對型別的解讀不一致
        long? val = BinaryMaskDecoder.ReadInt(_buf.AsSpan(0, _len), disc.offset, disc.type, _big);
        if (val == null) return 0;

        var variant = _spec.variants.FirstOrDefault(v => !v.isDefault && v.match == val.Value)
                   ?? _spec.variants.FirstOrDefault(v => v.isDefault);
        return variant?.length ?? 0;
    }

    private void Shift(int n)
    {
        if (n <= 0) return;
        if (n >= _len) { _len = 0; return; }
        Array.Copy(_buf, n, _buf, 0, _len - n);
        _len -= n;
    }

    private static int IndexOf(byte[] hay, int hayLen, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hayLen; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }



    private static byte[] ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
        if (hex.Length == 0 || hex.Length % 2 != 0) return Array.Empty<byte>();
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]))
                return Array.Empty<byte>();
        return b;
    }
}
