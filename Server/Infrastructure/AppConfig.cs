namespace EdgeLink.Infrastructure;

public class AppConfig
{
    public int    HttpPort         { get; init; } = 8081;
    public bool   HttpsEnabled     { get; init; }
    public int    HttpsPort        { get; init; } = 8443;
    public bool   InstallService   { get; init; }
    public bool   UninstallService { get; init; }

    public static AppConfig FromArgs(string[] args) => new()
    {
        HttpPort         = GetInt(args, "--port")       ?? GetEnvInt("EDGELINK_PORT")       ?? 8081,
        HttpsEnabled     = !HasFlag(args, "--no-https")  && GetEnv("EDGELINK_HTTPS") != "0",
        HttpsPort        = GetInt(args, "--https-port") ?? GetEnvInt("EDGELINK_HTTPS_PORT") ?? 8443,
        InstallService   = HasFlag(args, "--install"),
        UninstallService = HasFlag(args, "--uninstall"),
    };

    private static bool   HasFlag(string[] a, string f) => a.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
    private static string? GetEnv(string n)              => Environment.GetEnvironmentVariable(n);
    private static int?   GetEnvInt(string n)            => int.TryParse(GetEnv(n), out int v) ? v : null;

    private static int? GetInt(string[] a, string f)
    {
        int i = Array.FindIndex(a, x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) ? v : null;
    }
}
