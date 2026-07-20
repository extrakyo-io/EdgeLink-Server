using EdgeLink;
using EdgeLink.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var config = AppConfig.FromArgs(args);
AppConfig.WarnAboutRemovedHttpsFlags(args);

// ── Service install / uninstall ──────────────────────────────────────────────
if (config.InstallService)   { ServiceManager.Install(config); return; }
if (config.UninstallService) { ServiceManager.Uninstall();     return; }

// ── Host ─────────────────────────────────────────────────────────────────────
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    AppLogger.Error($"[Critical] Unhandled exception: {e.ExceptionObject}");

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    AppLogger.Error($"[Critical] Unobserved task: {e.Exception.Message}");
    e.SetObserved();
};

await Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = "EdgeLink")
    .ConfigureLogging(b => b.ClearProviders())   // AppLogger handles all output
    .ConfigureServices((_, services) =>
    {
        services.AddSingleton(config);
        services.AddHostedService<EdgeLinkService>();
    })
    .Build()
    .RunAsync();
