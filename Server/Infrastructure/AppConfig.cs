namespace EdgeLink.Infrastructure;

public class AppConfig
{
    public int    HttpPort         { get; init; } = 8081;
    public bool   InstallService   { get; init; }
    public bool   UninstallService { get; init; }

    public static AppConfig FromArgs(string[] args) => new()
    {
        HttpPort         = GetInt(args, "--port")       ?? GetEnvInt("EDGELINK_PORT")       ?? 8081,
        InstallService   = HasFlag(args, "--install"),
        UninstallService = HasFlag(args, "--uninstall"),
    };

    /// <summary>
    /// 已移除的 HTTPS 旗標。舊的服務註冊(binPath)與啟動腳本裡還帶著它們,
    /// 直接忽略會讓人以為 HTTPS 還開著,所以明確提醒一次。
    /// </summary>
    public static void WarnAboutRemovedHttpsFlags(string[] args)
    {
        string[] removed = { "--https", "--no-https", "--https-port" };
        var used = args.Where(a => removed.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (used.Length > 0)
            Console.WriteLine($"[Config] 已忽略 {string.Join(" ", used)} —— EdgeLink 不再提供 HTTPS," +
                              "請改用反向代理(IIS / nginx / Caddy)承接 TLS。");
        if (Environment.GetEnvironmentVariable("EDGELINK_HTTPS") != null ||
            Environment.GetEnvironmentVariable("EDGELINK_HTTPS_PORT") != null)
            Console.WriteLine("[Config] 已忽略 EDGELINK_HTTPS / EDGELINK_HTTPS_PORT 環境變數。");
    }

    private static bool   HasFlag(string[] a, string f) => a.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
    private static string? GetEnv(string n)              => Environment.GetEnvironmentVariable(n);
    private static int?   GetEnvInt(string n)            => int.TryParse(GetEnv(n), out int v) ? v : null;

    private static int? GetInt(string[] a, string f)
    {
        int i = Array.FindIndex(a, x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) ? v : null;
    }
}
