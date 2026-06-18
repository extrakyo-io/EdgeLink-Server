using System.Text;
using EdgeLink.Infrastructure;

namespace EdgeLink.NetworkServer.Services;

public static class SettingLoader
{
    private static string FilePath<T>() =>
        Path.Combine(AppPaths.SettingDir, typeof(T).Name + ".setting");

    public static T Load<T>() where T : new()
    {
        string path = FilePath<T>();
        if (!File.Exists(path)) return new T();
        string json = File.ReadAllText(path, Encoding.UTF8);
        return Json.FromJson<T>(json) ?? new T();
    }

    public static void Save<T>(T data)
    {
        Directory.CreateDirectory(AppPaths.SettingDir);
        File.WriteAllText(FilePath<T>(), Json.ToJson(data), Encoding.UTF8);
    }
}
