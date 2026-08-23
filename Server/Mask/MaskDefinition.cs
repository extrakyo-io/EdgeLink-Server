namespace EdgeLink.Mask;

[Serializable]
public class MaskDefinitions
{
    public List<MaskDefinition> definitions = new();
}

[Serializable]
public class MaskDefinition
{
    public string maskId = "";
    public string localizationKey = "";
    public string description = "";
    public string fieldDelimiter = "";
    public string kvSeparator = "";
    public string outputTemplate = "";
    public string sampleData = "";
    public string routeMode = "";
    public string correlationIdField = "";

    /// <summary>非 null 時,此 mask 為「二進位解析」模式:直接吃原始封包 bytes,
    /// 依 <see cref="BinarySpec"/> 解出欄位後套各 variant 的 template 產出文字(KV)。
    /// 文字路徑(fieldDelimiter/kvSeparator/outputTemplate)則忽略。</summary>
    public BinarySpec? binary;
}

// ── 通用二進位版面描述 ────────────────────────────────────────────────────────
// 用來把任意固定版面的二進位封包解析成命名欄位,再套 template 轉成 KV 文字。

[Serializable]
public class BinarySpec
{
    public string byteOrder = "little";        // "little" | "big"(多位元組欄位)
    public string sync = "";                   // 選填:TCP 串流分包用的對齊 magic(hex,如 "4f4b"='OK');UDP 免填
    public BinaryFieldRef? discriminator;      // 選填:讀某位址的值來挑 variant(如 msgType@3)

    /// <summary>選填:具名查表,給 <see cref="BinaryField.mapRef"/> 用。表名 → (值 → 文字)。
    /// key 可以是十進位、十六進位("0x2310")或範圍("0x0207-0x0249")。
    /// 放在 spec 這層而不是欄位內嵌,是為了讓多個欄位(例如三支軸的異警碼)共用同一份表。</summary>
    public Dictionary<string, Dictionary<string, string>>? maps;

    public List<BinaryVariant> variants = new();
}

// discriminator 位置(讀一個整數值來比對 variant.match)
[Serializable]
public class BinaryFieldRef
{
    public int offset;
    public string type = "u8";                 // u8/u16/u32/i8/i16/i32
}

[Serializable]
public class BinaryVariant
{
    public long match;                         // discriminator 值等於此才套用
    public bool isDefault;                     // true=不比對,永遠符合(無 discriminator 時用)
    public int length;                         // 期望封包長度;>0 且不符 → 丟棄
    public string template = "";               // 輸出樣板(KV),用 {欄位名};缺欄位 → 丟棄
    public List<BinaryField> fields = new();
}

[Serializable]
public class BinaryField
{
    public string name = "";
    public int offset;                         // 位元組位址(從封包起點)
    public string type = "u8";                 // u8/u16/u32/u64/i8/i16/i32/f32/f64/bit/bitrange/const
    public int bit;                            // bit/bitrange:起始位元(0=LSB)
    public int count = 1;                      // bitrange:位元數
    public string value = "";                  // const:直接輸出此字串
    public double scale = 1.0;                 // 數值型:輸出 = raw*scale + add
    public double add;
    public string format = "";                 // 選填數值格式(如 "0.###");空=整數原樣 / 浮點預設

    // ── 以下只在「編碼」(KV → 二進位)時有作用,解碼一律忽略 ──────────────────────
    /// <summary>編碼時自動填值,不從 KV 取:
    /// "seq"=每個 msgType 各一條、從 1 起算的遞增序號(連線重建歸 1);
    /// "timeMs"=當下 Unix epoch UTC 毫秒;"frameLength"=該 variant 的封包長度。
    /// 空字串=一般欄位(值來自 KV)。</summary>
    public string auto = "";
    /// <summary>編碼時 KV 缺這個欄位是否可接受。true=填 0(協定上的「沒用到請填 0」);
    /// false(預設)=整包丟棄。預設從嚴,因為靜默送出被歸零的命令比不送出去危險得多。</summary>
    public bool optional;

    // ── 以下只在「解碼」(二進位 → KV)時有作用 ──────────────────────────────────
    /// <summary>選填:指向 <see cref="BinarySpec.maps"/> 裡的表名,把讀到的原始值換成文字
    /// (查的是套 scale/add 之前的 wire 值 —— 這種表描述的是線上的代碼,不是工程值)。
    /// 帶 mapRef 的欄位在編碼時會被跳過:多對一的表反查不回去。</summary>
    public string mapRef = "";
    /// <summary>查不到對應時輸出的字串;留空則輸出原始數值。</summary>
    public string mapDefault = "";
}

/// <summary>編碼用的 seq 產生器:每個 msgType 各一條 counter、第一筆是 1
/// (0 保留給「從未發送」)。掛在連線上,重新連線時 <see cref="Reset"/> 歸零重來 ——
/// 對端是以「新的 TCP 連線 → 期望值重置回 1」來驗收的。</summary>
public sealed class BinarySeqCounters
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, uint> _counters = new();

    public uint Next(long msgType) =>
        _counters.AddOrUpdate(msgType, 1u, (_, v) => v == uint.MaxValue ? 1u : v + 1);

    public void Reset() => _counters.Clear();
}
