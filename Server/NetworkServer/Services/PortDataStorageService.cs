using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.TCP;

namespace EdgeLink.NetworkServer.Services;

public class PortDataStorageService
{
    public PortDatas LoadPortDatas()
    {
        try { return SettingLoader.Load<PortDatas>() ?? new PortDatas(); }
        catch (Exception ex)
        {
            AppLogger.Error($"[PortDataStorageService] Load failed: {ex.Message}");
            return new PortDatas();
        }
    }

    public List<PortData> LoadPortData() => LoadPortDatas().portDatas;

    public void SavePortDatas(PortDatas container)
    {
        try { SettingLoader.Save(container); }
        catch (Exception ex)
        {
            AppLogger.Error($"[PortDataStorageService] Save failed: {ex.Message}");
        }
    }

    public void SavePortData(List<PortData> data) =>
        SavePortDatas(new PortDatas { portDatas = data });

    public TcpClientRetryConfig LoadRetryConfig()
    {
        try { return SettingLoader.Load<TcpClientRetryConfig>() ?? new TcpClientRetryConfig(); }
        catch (Exception ex)
        {
            AppLogger.Error($"[PortDataStorageService] LoadRetryConfig failed: {ex.Message}");
            return new TcpClientRetryConfig();
        }
    }

    public void SaveRetryConfig(TcpClientRetryConfig config)
    {
        try { SettingLoader.Save(config); }
        catch (Exception ex)
        {
            AppLogger.Error($"[PortDataStorageService] SaveRetryConfig failed: {ex.Message}");
        }
    }
}
