using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Connector;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Services;
using EdgeLink.WebApi;
using Microsoft.Extensions.Hosting;

namespace EdgeLink;

public sealed class EdgeLinkService(AppConfig config) : BackgroundService
{
    private HttpApiServer? _http;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppLogger.Log("[EdgeLink] Starting...");

        var core = new NetworkConnectorCore();
        core.Init(MonitorSseHandler.Publish);

        PortManager.Initialize(core);
        PortManager.Instance.LoadAndStart();

        _http = new HttpApiServer();
        _http.Start(config, AppPaths.WebUiIndex);

        AppLogger.Log("[EdgeLink] Running.");
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        AppLogger.Log("[EdgeLink] Shutting down...");

        _http?.Stop();

        if (PortManager.IsInitialized)
            await PortManager.Instance.ShutdownAsync(TimeSpan.FromSeconds(15));

        LogHelper.Shutdown();
        AppLogger.Log("[EdgeLink] Stopped.");

        await base.StopAsync(cancellationToken);
    }
}
