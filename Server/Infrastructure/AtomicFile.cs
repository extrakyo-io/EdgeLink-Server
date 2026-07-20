using System.Text;

namespace EdgeLink.Infrastructure;

/// <summary>
/// 原子性的整檔覆寫。
///
/// File.WriteAllText 是「就地截斷再寫入」:寫到一半被中斷(服務停止、斷電、SCM 強制
/// 終止)就會留下截斷的 JSON,下次啟動解析失敗。先寫暫存檔再置換,可以保證讀到的
/// 永遠是完整的舊內容或完整的新內容。
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents, Encoding.UTF8);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else                   File.Move(tmp, path);
    }
}

/// <summary>
/// 設定檔「讀取」失敗(IO/權限/鎖檔),而不是「內容毀損」。
///
/// 這兩者必須分開處理:內容毀損時原檔已無價值,備份後改用預設值是對的;但讀取失敗
/// 時**檔案內容仍然完好**,若同樣退回預設值,接下來任何一次存檔都會把完好的設定
/// 覆蓋掉。呼叫端收到這個例外就必須停止寫入該檔。
/// </summary>
public sealed class SettingReadException : Exception
{
    public SettingReadException(string message, Exception inner) : base(message, inner) { }
}
