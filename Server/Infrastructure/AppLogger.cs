using Microsoft.Extensions.Logging;

namespace EdgeLink.Infrastructure;

public static class AppLogger
{
    private static readonly ILoggerFactory _factory = LoggerFactory.Create(b =>
    {
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        b.AddProvider(new RollingFileLoggerProvider(Path.Combine(AppPaths.DataDir, "Logs")));
        b.SetMinimumLevel(LogLevel.Debug);
    });

    private static readonly ILogger _default = _factory.CreateLogger("EdgeLink");

    public static ILogger<T> Create<T>()       => _factory.CreateLogger<T>();
    public static void Log(string msg)          => _default.LogInformation(msg);
    public static void Warning(string msg)      => _default.LogWarning(msg);
    public static void Error(string msg)        => _default.LogError(msg);
    public static void Exception(Exception ex)  => _default.LogError(ex, ex.Message);
}
