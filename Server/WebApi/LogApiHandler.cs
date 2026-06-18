using System.Net;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Logging;

namespace EdgeLink.WebApi;

public class LogApiHandler
{
    public Task GetLogsAsync(HttpListenerContext ctx)
    {
        string? cursorStr = ctx.Request.QueryString["cursor"];
        int cursor = int.TryParse(cursorStr, out var c) ? c : 0;
        var (total, logs) = LogHelper.GetConsoleLogs(cursor);
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new LogListResponse { total = total, logs = new List<string>(logs) }));
        return Task.CompletedTask;
    }

    public Task GetMonitorLogsAsync(HttpListenerContext ctx)
    {
        string? cursorStr = ctx.Request.QueryString["cursor"];
        int cursor = int.TryParse(cursorStr, out var c) ? c : 0;
        var (total, logs) = LogHelper.GetMonitorLogsSince(cursor);
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new LogListResponse { total = total, logs = new List<string>(logs) }));
        return Task.CompletedTask;
    }
}
