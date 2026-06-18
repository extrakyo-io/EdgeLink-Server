namespace EdgeLink.Infrastructure;

public static class AppPaths
{
    // 執行檔所在目錄（single-file publish 下用 ProcessPath，否則 BaseDirectory）
    public static string Root { get; } =
        Path.GetFullPath(
            Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
            ?? AppContext.BaseDirectory);

    // 設定檔目錄：<exe目錄>/Setting/
    public static string SettingDir { get; } =
        Path.Combine(Root, "Setting");

    // 資料目錄：<exe目錄>/Data/（等同 Application.persistentDataPath）
    public static string DataDir { get; } =
        Path.Combine(Root, "Data");

    // WebUI 目錄
    public static string WebUiDir { get; } =
        Path.Combine(Root, "WebUI");

    public static string WebUiIndex { get; } =
        Path.Combine(WebUiDir, "index.html");

    static AppPaths()
    {
        Directory.CreateDirectory(SettingDir);
        Directory.CreateDirectory(DataDir);
    }
}
