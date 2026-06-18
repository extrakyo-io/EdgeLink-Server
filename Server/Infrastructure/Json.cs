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

    public static T? FromJson<T>(string json) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(json, _opts); }
        catch { return new T(); }
    }
}
