using System.Net;
using System.Text;
using EdgeLink.Infrastructure;

namespace EdgeLink.WebApi;

public class HttpApiServer
{
    private HttpListener? _listener;
    private Thread?       _listenerThread;
    private volatile bool _running;
    private ApiRouter?    _router;

    public void Start(AppConfig config, string webUiPath)
    {
        _ = AuthManager.Instance;
        _router   = new ApiRouter(webUiPath);
        _listener = BuildAndStart(config);

        _running = true;
        _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "HttpApiServer" };
        _listenerThread.Start();
    }

    private static HttpListener BuildAndStart(AppConfig config)
    {
        // Refresh HTTP URL ACL (delete first to avoid conflicts from previous installs)
        RunNetsh($"http delete urlacl url=http://+:{config.HttpPort}/");
        RunNetsh($"http add urlacl url=http://+:{config.HttpPort}/ user=Everyone");

        var prefixes = new List<string> { $"http://+:{config.HttpPort}/" };

        if (config.HttpsEnabled)
        {
            // sslcert binding must already be registered via --install --https
            prefixes.Add($"https://+:{config.HttpsPort}/");
        }

        var listener = TryCreate(prefixes);
        if (listener != null) return listener;

        // Fall back to localhost if all-interface binding requires admin
        AppLogger.Warning(
            $"[HttpApiServer] Cannot bind to all interfaces. Falling back to localhost:{config.HttpPort}/. " +
            $"To enable LAN access run as Administrator or: " +
            $"netsh http add urlacl url=http://+:{config.HttpPort}/ user=Everyone");

        var fallback = new HttpListener();
        fallback.Prefixes.Add($"http://localhost:{config.HttpPort}/");
        fallback.Start();
        AppLogger.Log($"[HttpApiServer] Listening on http://localhost:{config.HttpPort}/");
        return fallback;
    }

    private static HttpListener? TryCreate(List<string> prefixes)
    {
        var l = new HttpListener();
        foreach (var p in prefixes) l.Prefixes.Add(p);
        try
        {
            l.Start();
            foreach (var p in prefixes) AppLogger.Log($"[HttpApiServer] Listening on {p}");
            return l;
        }
        catch (HttpListenerException ex)
        {
            AppLogger.Warning($"[HttpApiServer] Bind failed: {ex.Message}");
            try { l.Close(); } catch { }
            return null;
        }
    }

    private static void RunNetsh(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
        }
        catch (Exception ex) { AppLogger.Warning($"[HttpApiServer] netsh: {ex.Message}"); }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listenerThread?.Join(3000);
        AppLogger.Log("[HttpApiServer] Stopped");
    }

    private void ListenLoop()
    {
        while (_running)
        {
            try
            {
                var ctx = _listener!.GetContext();
                _ = Task.Run(() => HandleRequest(ctx));
            }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { AppLogger.Warning($"[HttpApiServer] ListenLoop: {ex.Message}"); }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"]  = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-API-Key";

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            await _router!.RouteAsync(ctx);
        }
        catch (Exception ex)
        {
            try { WriteError(ctx, 500, ex.Message); } catch { }
        }
    }

    public static void WriteError(HttpListenerContext ctx, int statusCode, string message)
        => WriteJson(ctx, statusCode, Json.ToJson(new ApiResult { success = false, error = message }));

    public static void WriteJson(HttpListenerContext ctx, int statusCode, string json)
    {
        try
        {
            byte[] buf = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode      = statusCode;
            ctx.Response.ContentType     = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        }
        finally { ctx.Response.Close(); }
    }
}
