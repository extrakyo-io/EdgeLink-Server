using System.Net;
using System.Text;

namespace EdgeLink.WebApi;

public class ApiRouter
{
    private readonly PortApiHandler    _portHandler    = new();
    private readonly MaskApiHandler    _maskHandler    = new();
    private readonly LogApiHandler     _logHandler     = new();
    private readonly MonitorApiHandler _monitorHandler = new();
    private readonly MonitorSseHandler _monitorSse     = new();
    private readonly AuthApiHandler    _authHandler    = new();
    private readonly SettingsApiHandler _settingsHandler = new();
    private readonly string _webUiPath;

    public ApiRouter(string webUiPath) => _webUiPath = webUiPath;

    public async Task RouteAsync(HttpListenerContext ctx)
    {
        string method    = ctx.Request.HttpMethod.ToUpperInvariant();
        string[] segments = ctx.Request.Url!.AbsolutePath.Trim('/').Split('/');

        if (method == "GET" && ctx.Request.Url.AbsolutePath == "/")
        {
            await ServeUiAsync(ctx); return;
        }
        if (method == "GET" && ctx.Request.Url.AbsolutePath == "/docs")
        {
            await ServeStaticAsync(ctx, "docs.html", "text/html; charset=utf-8"); return;
        }
        if (method == "GET" && ctx.Request.Url.AbsolutePath == "/openapi.json")
        {
            await ServeStaticAsync(ctx, "openapi.json", "application/json; charset=utf-8"); return;
        }
        if (method == "GET" && ctx.Request.Url.AbsolutePath == "/manual")
        {
            await ServeStaticAsync(ctx, "manual.html", "text/html; charset=utf-8"); return;
        }

        // /api/auth/*
        if (segments.Length >= 2 && segments[0] == "api" && segments[1] == "auth")
        {
            if (method == "POST" && segments.Length == 3 && segments[2] == "login")  { await _authHandler.LoginAsync(ctx);  return; }
            if (method == "POST" && segments.Length == 3 && segments[2] == "logout") { await _authHandler.LogoutAsync(ctx); return; }
            if (method == "GET"  && segments.Length == 3 && segments[2] == "status") { await _authHandler.StatusAsync(ctx); return; }

            if (!AuthManager.Instance.IsAuthenticated(ctx.Request))
            {
                HttpApiServer.WriteJson(ctx, 401, "{\"success\":false,\"error\":\"Unauthorized\"}");
                return;
            }
            if (method == "POST" && segments.Length == 3 && segments[2] == "change-password")
            {
                await _authHandler.ChangePasswordAsync(ctx); return;
            }
        }

        if (segments.Length >= 1 && segments[0] == "api")
        {
            if (!AuthManager.Instance.IsAuthenticated(ctx.Request))
            {
                HttpApiServer.WriteJson(ctx, 401, "{\"success\":false,\"error\":\"Unauthorized\"}");
                return;
            }
        }

        // /api/ports
        if (segments.Length >= 2 && segments[0] == "api" && segments[1] == "ports")
        {
            if (method == "GET"    && segments.Length == 2) { await _portHandler.GetAllAsync(ctx);    return; }
            if (method == "POST"   && segments.Length == 2) { await _portHandler.AddAsync(ctx);       return; }
            if (method == "DELETE" && segments.Length == 2) { await _portHandler.DeleteAsync(ctx);    return; }
            if (method == "PUT"    && segments.Length == 3)
            {
                await _portHandler.UpdateAsync(ctx, Uri.UnescapeDataString(segments[2])); return;
            }
            if (method == "POST" && segments.Length == 4 && segments[3] == "mask")
            {
                await _portHandler.ChangeMaskAsync(ctx, Uri.UnescapeDataString(segments[2])); return;
            }
            if (method == "POST" && segments.Length == 4 && segments[3] == "enabled")
            {
                await _portHandler.ToggleEnabledAsync(ctx, Uri.UnescapeDataString(segments[2])); return;
            }
            if (method == "GET" && segments.Length == 4 && segments[3] == "clients")
            {
                await _portHandler.GetClientsAsync(ctx, Uri.UnescapeDataString(segments[2])); return;
            }
        }

        // /api/masks
        if (segments.Length >= 2 && segments[0] == "api" && segments[1] == "masks")
        {
            if (method == "GET"  && segments.Length == 2) { await _maskHandler.GetAllAsync(ctx); return; }
            if (method == "POST" && segments.Length == 2) { await _maskHandler.AddAsync(ctx);    return; }
            if (method == "POST" && segments.Length == 4 && segments[3] == "rename")
            {
                await _maskHandler.RenameAsync(ctx, Uri.UnescapeDataString(segments[2])); return;
            }
            if (segments.Length == 3)
            {
                string maskId = Uri.UnescapeDataString(segments[2]);
                if (method == "GET")    { await _maskHandler.GetDefinitionAsync(ctx, maskId); return; }
                if (method == "PUT")    { await _maskHandler.SaveDefinitionAsync(ctx, maskId); return; }
                if (method == "DELETE") { await _maskHandler.DeleteAsync(ctx, maskId); return; }
            }
        }

        // /api/logs
        if (method == "GET" && segments.Length == 2 && segments[0] == "api" && segments[1] == "logs")
        {
            await _logHandler.GetLogsAsync(ctx); return;
        }
        if (method == "GET" && segments.Length == 2 && segments[0] == "api" && segments[1] == "monitor-logs")
        {
            await _logHandler.GetMonitorLogsAsync(ctx); return;
        }
        if (method == "GET" && segments.Length == 2 && segments[0] == "api" && segments[1] == "monitor-stream")
        {
            await _monitorSse.HandleAsync(ctx); return;
        }
        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "monitor" && segments[2] == "port")
        {
            if (method == "POST")   { await _monitorHandler.SetMonitorPortAsync(ctx);   return; }
            if (method == "DELETE") { await _monitorHandler.ClearMonitorPortAsync(ctx); return; }
            if (method == "GET")    { await _monitorHandler.GetMonitorPortAsync(ctx);   return; }
        }

        // /api/settings
        if (segments.Length == 3 && segments[0] == "api" && segments[1] == "settings")
        {
            if (method == "GET"  && segments[2] == "export") { await _settingsHandler.ExportAsync(ctx); return; }
            if (method == "POST" && segments[2] == "import") { await _settingsHandler.ImportAsync(ctx); return; }
        }

        HttpApiServer.WriteError(ctx, 404, $"Not found: {method} {ctx.Request.Url.AbsolutePath}");
    }

    private async Task ServeUiAsync(HttpListenerContext ctx)
    {
        try
        {
            byte[] buf = await Task.Run(() => File.ReadAllBytes(_webUiPath));
            ctx.Response.StatusCode      = 200;
            ctx.Response.ContentType     = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
        }
        catch (FileNotFoundException)
        {
            HttpApiServer.WriteError(ctx, 404, $"index.html not found at: {_webUiPath}");
        }
    }

    private async Task ServeStaticAsync(HttpListenerContext ctx, string fileName, string contentType)
    {
        string dir      = Path.GetDirectoryName(_webUiPath) ?? ".";
        string filePath = Path.Combine(dir, fileName);
        try
        {
            byte[] buf = await Task.Run(() => File.ReadAllBytes(filePath));
            ctx.Response.StatusCode      = 200;
            ctx.Response.ContentType     = contentType;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
        }
        catch (FileNotFoundException)
        {
            HttpApiServer.WriteError(ctx, 404, $"{fileName} not found");
        }
    }
}
