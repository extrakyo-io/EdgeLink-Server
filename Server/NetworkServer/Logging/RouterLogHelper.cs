using EdgeLink.NetworkServer.Base;
using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.NetworkServer.Logging;

public static class RouterLogHelper
{
    public static void LogReceive(PortData portData, MonitorTargetType targetType, string parsedMessage,
        System.Net.IPEndPoint? sourceEndpoint = null)
    {
        if (!MonitorManager.Instance.IsMonitoring(portData, targetType)) return;

        var count = MonitorCounter.Next();
        string fromTag = sourceEndpoint != null ? $" [from {sourceEndpoint.Address}:{sourceEndpoint.Port}]" : "";
        string shortId = !string.IsNullOrEmpty(portData.Id) ? " #" + portData.Id[..8] : "";
        LogHelper.LogToMonitor($"[#{count}] [Router] {GetTargetLabel(targetType)} [{portData.ProtocolName}{shortId}]{fromTag} Received: {parsedMessage}");
    }

    public static void LogSend(PortData portData, MonitorTargetType targetType, string parsedMessage)
    {
        if (!MonitorManager.Instance.IsMonitoring(portData, targetType)) return;

        var count = MonitorCounter.Next();
        string shortId = !string.IsNullOrEmpty(portData.Id) ? " #" + portData.Id[..8] : "";
        LogHelper.LogToMonitor($"[#{count}] [Router] {GetTargetLabel(targetType)} [{portData.ProtocolName}{shortId}] Sent: {parsedMessage}");
    }

    private static string GetTargetLabel(MonitorTargetType targetType) => targetType switch
    {
        MonitorTargetType.TCPServer => "TCP Server",
        MonitorTargetType.TCPClient => "TCP Client",
        MonitorTargetType.UDP       => "UDP",
        _                           => "Unknown"
    };
}
