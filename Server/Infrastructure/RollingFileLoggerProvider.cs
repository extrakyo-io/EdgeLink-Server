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
        PurgeOld();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_logDir, _lock);
    public void Dispose() { }

    private void PurgeOld()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_keepDays);
            foreach (var f in Directory.GetFiles(_logDir, "edgelink-*.log"))
                if (File.GetCreationTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { }
    }
}

internal sealed class FileLogger(string logDir, object fileLock) : ILogger
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

        var path = Path.Combine(logDir, $"edgelink-{DateTime.UtcNow:yyyy-MM-dd}.log");
        lock (fileLock)
        {
            try { File.AppendAllText(path, sb + Environment.NewLine); }
            catch { }
        }
    }
}
