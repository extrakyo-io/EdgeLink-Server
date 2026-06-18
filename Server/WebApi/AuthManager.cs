using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EdgeLink.Infrastructure;

namespace EdgeLink.WebApi;

public class AuthManager
{
    private static AuthManager? _instance;
    public static AuthManager Instance => _instance ??= new AuthManager();

    private string _passwordHash = "";
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly TimeSpan   _sessionTimeout = TimeSpan.FromHours(8);
    private readonly string     _settingsPath;
    private readonly string     _sessionsPath;

    private AuthManager()
    {
        _settingsPath = Path.Combine(AppPaths.DataDir, "auth.json");
        _sessionsPath = Path.Combine(AppPaths.DataDir, "sessions.json");
        LoadSettings();
        LoadSessions();
    }

    // ── Password ─────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var s = Json.FromJson<AuthSettings>(File.ReadAllText(_settingsPath));
                if (s != null && !string.IsNullOrEmpty(s.passwordHash))
                {
                    _passwordHash = s.passwordHash;
                    return;
                }
            }
            catch (Exception ex) { AppLogger.Warning($"[Auth] Failed to load settings: {ex.Message}"); }
        }
        _passwordHash = HashPassword("admin");
        SaveSettings();
        AppLogger.Log("[Auth] First run — default password: admin");
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, Json.ToJson(new AuthSettings { passwordHash = _passwordHash }));
        }
        catch (Exception ex) { AppLogger.Warning($"[Auth] Failed to save settings: {ex.Message}"); }
    }

    public void ChangePassword(string newPassword)
    {
        _passwordHash = HashPassword(newPassword);
        SaveSettings();
        // Invalidate all sessions on password change
        _sessions.Clear();
        SaveSessions();
    }

    public bool ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        if (!VerifyPassword(password, _passwordHash)) return false;

        // Migrate legacy SHA-256 hash to PBKDF2 on first successful login
        if (!_passwordHash.StartsWith("v2:", StringComparison.Ordinal))
        {
            _passwordHash = HashPassword(password);
            SaveSettings();
        }
        return true;
    }

    // PBKDF2-SHA256 with random salt, 100 000 iterations
    // Format: "v2:{iterations}:{base64salt}:{base64hash}"
    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var kdf = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        return $"v2:100000:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(kdf.GetBytes(32))}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        if (stored.StartsWith("v2:", StringComparison.Ordinal))
        {
            var p = stored.Split(':');
            if (p.Length != 4 || !int.TryParse(p[1], out int iter)) return false;
            byte[] salt     = Convert.FromBase64String(p[2]);
            byte[] expected = Convert.FromBase64String(p[3]);
            using var kdf   = new Rfc2898DeriveBytes(password, salt, iter, HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(kdf.GetBytes(32), expected);
        }
        // Legacy SHA-256 (no salt) — accepted for one-time migration
        using var sha   = SHA256.Create();
        string legacy   = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(password)))
            .Replace("-", "").ToLowerInvariant();
        return legacy == stored;
    }

    // ── Sessions ─────────────────────────────────────────────────────────────

    private void LoadSessions()
    {
        try
        {
            if (!File.Exists(_sessionsPath)) return;
            var list = Json.FromJson<List<SessionEntry>>(File.ReadAllText(_sessionsPath));
            if (list == null) return;
            var now = DateTime.UtcNow;
            foreach (var e in list)
            {
                var expiry = DateTimeOffset.FromUnixTimeSeconds(e.exp).UtcDateTime;
                if (expiry > now) _sessions[e.sid] = expiry;
            }
        }
        catch (Exception ex) { AppLogger.Warning($"[Auth] Failed to load sessions: {ex.Message}"); }
    }

    private void SaveSessions()
    {
        try
        {
            var now  = DateTime.UtcNow;
            var list = _sessions
                .Where(kv => kv.Value > now)
                .Select(kv => new SessionEntry
                {
                    sid = kv.Key,
                    exp = new DateTimeOffset(kv.Value).ToUnixTimeSeconds()
                })
                .ToList();
            File.WriteAllText(_sessionsPath, Json.ToJson(list));
        }
        catch (Exception ex) { AppLogger.Warning($"[Auth] Failed to save sessions: {ex.Message}"); }
    }

    public string CreateSession()
    {
        string sid = Guid.NewGuid().ToString("N");
        _sessions[sid] = DateTime.UtcNow + _sessionTimeout;
        SaveSessions();
        return sid;
    }

    public bool ValidateSession(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        if (!_sessions.TryGetValue(sid, out var expiry)) return false;
        if (expiry < DateTime.UtcNow) { _sessions.TryRemove(sid, out _); return false; }
        _sessions[sid] = DateTime.UtcNow + _sessionTimeout;
        return true;
    }

    public void DestroySession(string sid)
    {
        if (!string.IsNullOrEmpty(sid))
        {
            _sessions.TryRemove(sid, out _);
            SaveSessions();
        }
    }

    public bool IsAuthenticated(HttpListenerRequest request)
    {
        var cookie = request.Cookies["edgelink_sid"];
        return cookie != null && ValidateSession(cookie.Value);
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private class AuthSettings  { public string passwordHash = ""; }
    private class SessionEntry  { public string sid = ""; public long exp; }
}
