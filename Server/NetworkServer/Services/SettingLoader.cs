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

        // 讀檔與解析必須分開判斷。先前 ReadAllText 在 try 之外,IO 失敗(防毒/備份軟體
        // 開檔、暫時性權限錯誤)會直接往上拋、繞過下面整段備份邏輯,被呼叫端的
        // catch-all 接住後退回空設定 —— 而檔案其實完好無損,使用者一存檔就永久覆蓋。
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingReadException(
                $"{Path.GetFileName(path)} 讀取失敗(檔案內容未受影響,已停止對該檔的寫入):{ex.Message}", ex);
        }

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

        AtomicFile.WriteAllText(path, Json.ToJson(data));
    }
}
