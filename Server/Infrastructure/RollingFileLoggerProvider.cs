using Microsoft.Extensions.Logging;
using System.Text;

namespace EdgeLink.Infrastructure;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly int    _keepDays;
    private static readonly object _lock = new();

    public RollingFileLoggerProvider(string logDir, int keepDays = 7)
    {
        _logDir   = logDir;
        _keepDays = keepDays;
        Directory.CreateDirectory(logDir);
        LogRetention.PurgeIfNewDay(_logDir, _keepDays, DateTime.UtcNow);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_logDir, _lock, _keepDays);
    public void Dispose() { }
}

/// <summary>
/// 日誌保留期清理。
///
/// 先前只在 provider 的建構子執行一次 —— 這是設計成長期運行的 Windows 服務,
/// 跑 90 天就會累積 90 個日誌檔一個都不會刪,因為唯一一次清理發生在第 0 天,
/// 那時根本還沒有任何檔案超過保留期。改為每次跨日(檔案輪替時)再清一次。
/// </summary>
internal static class LogRetention
{
    private static DateTime _lastPurgedDateUtc = DateTime.MinValue;

    /// <summary>跨到新的一天才會真的清理。呼叫端已持有檔案鎖。</summary>
    internal static void PurgeIfNewDay(string logDir, int keepDays, DateTime nowUtc)
    {
        if (_lastPurgedDateUtc == nowUtc.Date) return;
        _lastPurgedDateUtc = nowUtc.Date;

        try
        {
            var cutoff = nowUtc.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(logDir, "edgelink-*.log"))
                if (File.GetCreationTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { }
    }
}

internal sealed class FileLogger(string logDir, object fileLock, int keepDays) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;

        string lvl = level switch
        {
            LogLevel.Warning  => "WARN",
            LogLevel.Error    => "FAIL",
            LogLevel.Critical => "CRIT",
            _                 => "INFO",
        };

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append(" [").Append(lvl).Append("] ");
        sb.Append(formatter(state, ex));
        if (ex != null) sb.AppendLine().Append(ex);

        var nowUtc = DateTime.UtcNow;
        var path   = Path.Combine(logDir, $"edgelink-{nowUtc:yyyy-MM-dd}.log");
        lock (fileLock)
        {
            // 檔案每天輪替,跨日時順便清掉超過保留期的舊檔
            LogRetention.PurgeIfNewDay(logDir, keepDays, nowUtc);
            try { File.AppendAllText(path, sb + Environment.NewLine); }
            catch { }
        }
    }
}
