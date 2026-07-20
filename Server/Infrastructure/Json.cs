using System.Text.Json;

namespace EdgeLink.Infrastructure;

// 替換 UnityEngine.JsonUtility
public static class Json
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    public static string ToJson<T>(T obj) =>
        JsonSerializer.Serialize(obj, _opts);

    /// <summary>反序列化。解析失敗會拋出例外,呼叫端必須自行處理。
    /// 先前這裡吞掉所有例外並回傳 new T(),導致:壞掉的設定檔被當成「空值」載入、
    /// 下次存檔就把使用者資料覆蓋掉;API handler 的 400 錯誤處理也形同死碼。</summary>
    public static T? FromJson<T>(string json) where T : new() =>
        JsonSerializer.Deserialize<T>(json, _opts);
}
