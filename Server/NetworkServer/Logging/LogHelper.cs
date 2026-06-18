using System.Collections.Concurrent;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.Logging;

/// <summary>
/// 應用程式日誌中樞：環形緩衝區供 Web API 讀取，同時輸出到 AppLogger。
/// Console App 版不需要 Unity 主執行緒派發。
/// </summary>
public static class LogHelper
{
    private static volatile bool _shuttingDown;

    // Console log 環形緩衝區（供 /api/logs）
    private const int WebLogBuffer = 200;
    private static readonly string[] _consoleBuf = new string[WebLogBuffer];
    private static int _consoleHead;
    private static int _consoleTotal;
    private static readonly object _consoleLock = new();

    // Monitor log 環形緩衝區（供 /api/monitor-logs 與 SSE）
    private const int WebMonitorBuffer = 500;
    private static readonly string[] _monitorBuf = new string[WebMonitorBuffer];
    private static int _monitorHead;
    private static int _monitorTotal;
    private static readonly object _monitorLock = new();

    // SSE 推播
    private static Action<string>? _ssePublish;

    public static void Init(Action<string>? ssePublish = null)
    {
        _shuttingDown = false;
        _ssePublish = ssePublish;
    }

    public static void Shutdown() => _shuttingDown = true;

    public static void LogToConsole(string message, bool isError = false)
    {
        if (_shuttingDown) return;
        if (message.Length > 1000) message = message[..1000] + "...(truncated)";
        string stamped = Format(message, isError);

        lock (_consoleLock)
        {
            _consoleBuf[_consoleHead] = stamped;
            _consoleHead = (_consoleHead + 1) % WebLogBuffer;
            _consoleTotal++;
        }

        if (isError) AppLogger.Error(stamped);
        else         AppLogger.Log(stamped);
    }

    public static void LogToMonitor(string message)
    {
        if (_shuttingDown) return;
        string stamped = Format(message, false);

        lock (_monitorLock)
        {
            _monitorBuf[_monitorHead] = stamped;
            _monitorHead = (_monitorHead + 1) % WebMonitorBuffer;
            _monitorTotal++;
        }

        _ssePublish?.Invoke(stamped);
    }

    public static (int total, string[] logs) GetConsoleLogs(int cursor)
    {
        lock (_consoleLock) return GetSince(_consoleBuf, _consoleHead, _consoleTotal, WebLogBuffer, cursor);
    }

    public static (int total, string[] logs) GetMonitorLogsSince(int cursor)
    {
        lock (_monitorLock) return GetSince(_monitorBuf, _monitorHead, _monitorTotal, WebMonitorBuffer, cursor);
    }

    // 相容舊名稱
    public static (int total, string[] logs) GetWebMonitorLogsSince(int cursor) =>
        GetMonitorLogsSince(cursor);

    public static string Tag(string protocol, string name) => $"[{protocol} | {name}]";

    public static string Tag(string protocol, PortData? portData)
    {
        string shortId = !string.IsNullOrEmpty(portData?.Id) ? " #" + portData.Id[..8] : "";
        return $"[{protocol} | {portData?.ProtocolName}{shortId}]";
    }

    private static string Format(string msg, bool isError) =>
        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {(isError ? "[Error]" : "[Info]")} {msg}";

    private static (int total, string[] logs) GetSince(string[] buf, int head, int total, int bufSize, int cursor)
    {
        if (cursor >= total) return (total, Array.Empty<string>());
        int available = Math.Min(total - cursor, bufSize);
        int startSlot = (head - available + bufSize * 2) % bufSize;
        var result = new string[available];
        for (int i = 0; i < available; i++)
            result[i] = buf[(startSlot + i) % bufSize] ?? "";
        return (total, result);
    }
}
