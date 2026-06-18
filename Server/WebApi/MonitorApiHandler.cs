using System.Net;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Logging;
using EdgeLink.NetworkServer.Services;

namespace EdgeLink.WebApi;

public class MonitorApiHandler
{
    public async Task SetMonitorPortAsync(HttpListenerContext ctx)
    {
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8))
            body = await sr.ReadToEndAsync();

        MonitorPortReq? req;
        try { req = Json.FromJson<MonitorPortReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (string.IsNullOrWhiteSpace(req?.id)) { HttpApiServer.WriteError(ctx, 400, "id is required"); return; }

        try
        {
            var port = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == req.id);
            if (port == null) throw new KeyNotFoundException($"Port '{req.id}' not found");
            PortManager.Instance.OnMonitorConsole(port);
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex) { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (Exception ex)            { HttpApiServer.WriteError(ctx, 400, ex.Message); }
    }

    public Task ClearMonitorPortAsync(HttpListenerContext ctx)
    {
        MonitorManager.Instance.SetMonitorPort(null!, MonitorTargetType.UDP);
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        return Task.CompletedTask;
    }

    public Task GetMonitorPortAsync(HttpListenerContext ctx)
    {
        var (portData, _) = MonitorManager.Instance.GetMonitorInfo();
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new MonitorPortResponse { protocolName = portData?.ProtocolName ?? "" }));
        return Task.CompletedTask;
    }
}
