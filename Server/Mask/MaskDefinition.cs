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
    public BinaryFieldRef? discriminator;      // 選填:讀某位址的值來挑 variant(如 msgType@3)
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
}
