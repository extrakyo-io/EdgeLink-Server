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
                EnterDegradedMode("auth.json 內容無效(缺少 passwordHash)");
                return;
            }
            catch (Exception ex)
            {
                // 先前這裡會掉到下面的「首次執行」分支:密碼被重設為 admin 並**立刻覆寫
                // 原檔**。改密碼當下斷電留下截斷的 JSON,或檔案被防毒暫時鎖住,都會讓
                // Web UI 悄悄退回預設密碼可登入 —— 既是資料遺失也是安全降級。
                // 檔案存在就代表使用者設過密碼,絕不能自動退回預設值。
                EnterDegradedMode($"auth.json 載入失敗:{ex.Message}");
                return;
            }
        }

        // 只有「檔案真的不存在」才是首次執行
        _passwordHash = HashPassword("admin");
        SaveSettings();
        AppLogger.Log("[Auth] First run — default password: admin");
    }

    /// <summary>
    /// auth.json 存在但讀不出來時進入的狀態:不覆寫該檔、也不接受任何登入。
    ///
    /// 退回預設密碼會讓任何人用 admin 登入(安全降級);沿用空雜湊則會讓
    /// VerifyPassword 的行為取決於實作細節。兩者都比「明確拒絕並要求人工處理」差。
    /// 排除問題(還原檔案或修正權限)後重啟服務即可恢復。
    /// </summary>
    private void EnterDegradedMode(string reason)
    {
        IsDegraded    = true;
        _passwordHash = "";
        AppLogger.Error($"[Auth] {reason} —— 已停用登入且不覆寫 auth.json。" +
                        "請還原該檔或修正檔案權限後重啟服務。");
    }

    /// <summary>auth.json 無法載入,登入一律拒絕(見 <see cref="EnterDegradedMode"/>)。</summary>
    public bool IsDegraded { get; private set; }

    private void SaveSettings()
    {
        try
        {
            AtomicFile.WriteAllText(_settingsPath, Json.ToJson(new AuthSettings { passwordHash = _passwordHash }));
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
        if (IsDegraded) return false;
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

    /// <summary>序列化 sessions.json 的寫入。併發登入會同時觸發 SaveSessions,
    /// 同一路徑的並行寫入會互相撞成 sharing violation 並被吞掉 —— session 只留在
    /// 記憶體,重啟後全部掉線。</summary>
    private readonly object _sessionsFileLock = new();

    private void SaveSessions()
    {
        lock (_sessionsFileLock)
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
            AtomicFile.WriteAllText(_sessionsPath, Json.ToJson(list));
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
