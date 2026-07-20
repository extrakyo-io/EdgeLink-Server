using System.Diagnostics;
using System.Security.Principal;

namespace EdgeLink.Infrastructure;

public static class ServiceManager
{
    private const string Name        = "EdgeLink";
    private const string DisplayName = "EdgeLink IoT Gateway";
    private const string Description = "EdgeLink protocol bridge and IoT gateway server.";

    public static void Install(AppConfig config)
    {
        if (!IsAdmin())
        {
            Console.Error.WriteLine("[Install] Must be run as Administrator.");
            Environment.Exit(1);
        }

        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName;

        // 先前這裡串的是 --https,但 AppConfig 根本沒有這個旗標(它認的是 --no-https),
        // 所以 --install --no-https 安裝出來的服務反而會把 HTTPS 打開、且沒有繫結憑證。
        // HTTPS 已整個移除,binPath 只留 --port。
        string binPath = $"\"{exePath}\" --port {config.HttpPort}";

        // Stop + delete existing service first (idempotent install)
        Sc($"stop {Name}");
        System.Threading.Thread.Sleep(2000);
        Sc($"delete {Name}");
        System.Threading.Thread.Sleep(1000);

        Sc($"create {Name} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
        Sc($"description {Name} \"{Description}\"");
        Sc($"failure {Name} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        // Delete then re-register URL ACLs (avoid conflicts from previous installs)
        RunNetsh($"http delete urlacl url=http://+:{config.HttpPort}/");
        RunNetsh($"http add urlacl url=http://+:{config.HttpPort}/ user=Everyone");

        // 從舊版升級上來的機器,Trusted Root 裡還留著那張私鑰密碼公開的憑證。
        // 光是不再產生新憑證並不會讓既有的風險消失,所以安裝時一併清掉。
        LegacyHttpsCleanup.Run();

        Console.WriteLine($"[Install] Service '{Name}' installed. Run: sc start {Name}");
    }

    public static void Uninstall()
    {
        if (!IsAdmin())
        {
            Console.Error.WriteLine("[Uninstall] Must be run as Administrator.");
            Environment.Exit(1);
        }
        Sc($"stop {Name}");
        Sc($"delete {Name}");
        LegacyHttpsCleanup.Run();
        Console.WriteLine($"[Uninstall] Service '{Name}' removed.");
    }

    private static void Sc(string args)      => Run("sc",    args);
    private static void RunNetsh(string args) => Run("netsh", args);

    private static void Run(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            });
            p?.WaitForExit(10_000);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[{exe}] {args}: {ex.Message}"); }
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
