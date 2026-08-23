using System.Text;
using EdgeLink.Infrastructure;

namespace EdgeLink.Mask;

public static class MaskProcessor
{
    public static string Process(MaskDefinition? def, byte[] rawBytes, string textMessage, Dictionary<string, string>? extraFields = null)
    {
        if (def == null) return textMessage;

        // 二進位 mask:直接吃原始封包 bytes(呼叫端須傳未經 UTF8 破壞的原始 datagram)
        if (def.binary != null)
            return BinaryMaskDecoder.Decode(rawBytes, def.binary) ?? "";

        if (def.outputTemplate == "{raw}" || string.IsNullOrEmpty(def.outputTemplate))
            return textMessage;

        Dictionary<string, string> fields;
        try { fields = ExtractTextFields(def, textMessage); }
        catch (Exception ex)
        {
            AppLogger.Warning($"[MaskProcessor] 欄位解析失敗 ({def.maskId}): {ex}");
            return "";
        }

        if (extraFields != null)
            foreach (var kv in extraFields) fields[kv.Key] = kv.Value;

        return TemplateRenderer.Render(def, def.outputTemplate, fields);
    }

    /// <summary>
    /// 輸出成「要送上線的位元組」。這是 <see cref="Process"/> 的出站版本,兩者對 binary mask
    /// 的解讀方向相反:同一份 <see cref="BinarySpec"/> 在入站是解碼(wire → KV)、在出站是
    /// 編碼(KV → wire)。方向由呼叫端決定,不是由 mask 決定。
    /// <para>純文字 mask 的行為與先前完全相同(套樣板 + 補換行)。回傳 null = 這筆不送。</para>
    /// </summary>
    public static byte[]? ProcessToBytes(MaskDefinition? def, byte[] rawBytes, string textMessage,
        BinarySeqCounters? seq = null, Dictionary<string, string>? extraFields = null)
    {
        if (def?.binary != null)
        {
            Dictionary<string, string> fields;
            try { fields = ExtractTextFields(def, textMessage); }
            catch (Exception ex)
            {
                AppLogger.Warning($"[MaskProcessor] 欄位解析失敗 ({def.maskId}): {ex}");
                return null;
            }
            if (extraFields != null)
                foreach (var kv in extraFields) fields[kv.Key] = kv.Value;

            return BinaryMaskEncoder.Encode(fields, def.binary, seq);
        }

        string output = Process(def, rawBytes, textMessage, extraFields);
        if (string.IsNullOrEmpty(output)) return null;
        return Encoding.UTF8.GetBytes(output.EndsWith('\n') ? output : output + "\n");
    }

    private static Dictionary<string, string> ExtractTextFields(MaskDefinition def, string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;

        var fieldDelim = string.IsNullOrEmpty(def.fieldDelimiter) ? ";" : def.fieldDelimiter;
        var kvSep      = string.IsNullOrEmpty(def.kvSeparator)    ? ":" : def.kvSeparator;

        foreach (var field in text.Split([fieldDelim], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = field.IndexOf(kvSep, StringComparison.Ordinal);
            if (idx < 0) continue;
            var key = field[..idx].Trim();
            var val = field[(idx + kvSep.Length)..].Trim();
            if (!string.IsNullOrEmpty(key)) result[key] = val;
        }
        return result;
    }

}
