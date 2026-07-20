using System.Net;
using System.Text;
using System.Text.Json;

namespace EdgeLink.WebApi;

public class AuthApiHandler
{
    public async Task LoginAsync(HttpListenerContext ctx)
    {
        string client = LoginThrottle.ClientKey(ctx.Request);

        // 鎖定中就直接回 429 —— 連 PBKDF2 都不執行,避免被當成 CPU 放大器
        if (LoginThrottle.Instance.IsLockedOut(client, out int retryAfter))
        {
            ctx.Response.AddHeader("Retry-After", retryAfter.ToString());
            HttpApiServer.WriteJson(ctx, 429,
                $"{{\"success\":false,\"error\":\"Too many failed attempts, retry in {retryAfter}s\"}}");
            return;
        }

        try
        {
            string? body = await HttpApiServer.ReadBodyAsync(ctx);
            if (body == null) return;   // 已回 413

            using var doc = JsonDocument.Parse(body);
            string password = doc.RootElement.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";

            if (!AuthManager.Instance.ValidatePassword(password))
            {
                LoginThrottle.Instance.RecordFailure(client);
                HttpApiServer.WriteJson(ctx, 401, "{\"success\":false,\"error\":\"Invalid password\"}");
                return;
            }
            LoginThrottle.Instance.RecordSuccess(client);
            string sid = AuthManager.Instance.CreateSession();
            ctx.Response.SetCookie(new Cookie("edgelink_sid", sid)
            {
                HttpOnly = true, Path = "/",
                Expires = DateTime.UtcNow.AddHours(8)
            });
            HttpApiServer.WriteJson(ctx, 200, "{\"success\":true}");
        }
        catch { HttpApiServer.WriteJson(ctx, 400, "{\"success\":false,\"error\":\"Bad request\"}"); }
    }

    public Task LogoutAsync(HttpListenerContext ctx)
    {
        var cookie = ctx.Request.Cookies["edgelink_sid"];
        if (cookie != null) AuthManager.Instance.DestroySession(cookie.Value);
        ctx.Response.SetCookie(new Cookie("edgelink_sid", "")
        {
            HttpOnly = true, Path = "/",
            Expires = DateTime.UtcNow.AddDays(-1)
        });
        HttpApiServer.WriteJson(ctx, 200, "{\"success\":true}");
        return Task.CompletedTask;
    }

    public Task StatusAsync(HttpListenerContext ctx)
    {
        bool authed = AuthManager.Instance.IsAuthenticated(ctx.Request);
        HttpApiServer.WriteJson(ctx, 200, $"{{\"authenticated\":{(authed ? "true" : "false")}}}");
        return Task.CompletedTask;
    }

    public async Task ChangePasswordAsync(HttpListenerContext ctx)
    {
        try
        {
            string? body = await HttpApiServer.ReadBodyAsync(ctx);
            if (body == null) return;   // 已回 413
            using var doc = JsonDocument.Parse(body);
            string current = doc.RootElement.TryGetProperty("currentPassword", out var c) ? c.GetString() ?? "" : "";
            string next    = doc.RootElement.TryGetProperty("newPassword",     out var n) ? n.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(next))
            {
                HttpApiServer.WriteJson(ctx, 400, "{\"success\":false,\"error\":\"Bad request\"}");
                return;
            }
            if (!AuthManager.Instance.ValidatePassword(current))
            {
                HttpApiServer.WriteJson(ctx, 401, "{\"success\":false,\"error\":\"Invalid current password\"}");
                return;
            }
            AuthManager.Instance.ChangePassword(next);
            HttpApiServer.WriteJson(ctx, 200, "{\"success\":true}");
        }
        catch { HttpApiServer.WriteJson(ctx, 400, "{\"success\":false,\"error\":\"Bad request\"}"); }
    }
}
