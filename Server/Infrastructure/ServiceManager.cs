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

        string binPath = $"\"{exePath}\" --port {config.HttpPort}" +
            (config.HttpsEnabled ? $" --https --https-port {config.HttpsPort}" : "");

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
        if (config.HttpsEnabled)
        {
            RunNetsh($"http delete urlacl url=https://+:{config.HttpsPort}/");
            RunNetsh($"http add urlacl url=https://+:{config.HttpsPort}/ user=Everyone");

            // Cert setup MUST happen here (interactive admin session) — not from the service
            Console.WriteLine("[Install] Setting up HTTPS certificate...");
            var cert = CertificateHelper.GetOrCreate();
            CertificateHelper.EnsureHttpsBound(cert, config.HttpsPort);
        }

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
