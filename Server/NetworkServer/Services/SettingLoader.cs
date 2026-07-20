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
        try
        {
            return Json.FromJson<T>(json) ?? new T();
        }
        catch (Exception ex)
        {
            // 設定檔毀損:先把原檔另存備份,再回預設值。
            // 若直接回預設值而不備份,後續任何一次 Save 都會用空內容覆蓋掉使用者資料。
            string backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(path, backup); }
            catch (Exception mv) { AppLogger.Error($"[SettingLoader] 備份毀損檔失敗:{mv.Message}"); }

            AppLogger.Error($"[SettingLoader] {Path.GetFileName(path)} 解析失敗,已備份為 " +
                            $"{Path.GetFileName(backup)} 並改用預設值:{ex.Message}");
            return new T();
        }
    }

    public static void Save<T>(T data)
    {
        Directory.CreateDirectory(AppPaths.SettingDir);
        string path = FilePath<T>();

        // 原子寫入:先寫暫存檔再置換。直接 WriteAllText 是就地截斷,
        // 寫到一半被中斷(服務停止/斷電)會留下截斷的 JSON,下次啟動就載入失敗。
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Json.ToJson(data), Encoding.UTF8);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else                   File.Move(tmp, path);
    }
}
