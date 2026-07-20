using System.Collections.Concurrent;
using System.Net;

namespace EdgeLink.WebApi;

/// <summary>
/// 登入失敗節流(依來源 IP)。
///
/// 先前 /api/auth/login 完全沒有任何限制,而每次驗證要跑 100,000 次 PBKDF2,
/// 造成兩個問題:
///   1. 可以無限次線上猜密碼(預設密碼還是 admin)
///   2. 不對稱 CPU 放大 —— 幾百個併發請求就能把 thread pool 餓死,
///      連帶讓跑在同一個 pool 上的連接器迴圈停擺
/// 鎖定期間直接回 429,連 PBKDF2 都不會執行。
/// </summary>
public sealed class LoginThrottle
{
    private static LoginThrottle? _instance;
    public static LoginThrottle Instance => _instance ??= new LoginThrottle();

    /// <summary>連續失敗幾次後開始鎖定。</summary>
    private const int FailuresBeforeLockout = 5;
    private const int BaseLockoutSeconds    = 30;
    private const int MaxLockoutSeconds     = 15 * 60;
    /// <summary>多久沒有新的失敗就把紀錄視為過期。</summary>
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public int      Failures;
        public DateTime LockedUntilUtc;
        public DateTime LastSeenUtc = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public static string ClientKey(HttpListenerRequest req) =>
        req.RemoteEndPoint?.Address?.ToString() ?? "unknown";

    /// <summary>是否仍在鎖定中;是的話 retryAfter 為剩餘秒數。</summary>
    public bool IsLockedOut(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!_entries.TryGetValue(key, out var e)) return false;

        var now = DateTime.UtcNow;
        if (e.LockedUntilUtc <= now) return false;

        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((e.LockedUntilUtc - now).TotalSeconds));
        return true;
    }

    public void RecordFailure(string key)
    {
        var now = DateTime.UtcNow;
        var e = _entries.AddOrUpdate(key,
            _ => new Entry { Failures = 1, LastSeenUtc = now },
            (_, old) => { old.Failures++; old.LastSeenUtc = now; return old; });

        if (e.Failures >= FailuresBeforeLockout)
        {
            // 每多失敗一次就加倍,上限 15 分鐘
            int over    = e.Failures - FailuresBeforeLockout;
            int seconds = (int)Math.Min(MaxLockoutSeconds, BaseLockoutSeconds * Math.Pow(2, Math.Min(over, 10)));
            e.LockedUntilUtc = now.AddSeconds(seconds);
        }

        Prune(now);
    }

    /// <summary>登入成功 → 清掉該來源的失敗紀錄。</summary>
    public void RecordSuccess(string key) => _entries.TryRemove(key, out _);

    private void Prune(DateTime now)
    {
        if (_entries.Count < 1024) return;   // 平時不做,避免每次登入都掃
        foreach (var kv in _entries)
            if (now - kv.Value.LastSeenUtc > EntryTtl && kv.Value.LockedUntilUtc <= now)
                _entries.TryRemove(kv.Key, out _);
    }

    /// <summary>清空所有鎖定紀錄(維運手動解鎖 / 測試用)。</summary>
    public void Reset() => _entries.Clear();
}
