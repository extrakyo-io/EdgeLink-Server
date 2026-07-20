using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.TCP;

namespace EdgeLink.NetworkServer.Services;

public class PortDataStorageService
{

    /// <summary>
    /// 讀取失敗過就不再寫入。讀取失敗代表檔案內容仍然完好(只是當下拿不到),
    /// 此時記憶體裡是空的預設值,任何一次存檔都會把使用者的設定永久覆蓋掉。
    /// 需要重啟服務(且問題排除後)才會恢復寫入。
    /// </summary>
    private bool _readFailed;
    public PortDatas LoadPortDatas()
    {
        try { return SettingLoader.Load<PortDatas>() ?? new PortDatas(); }
        catch (SettingReadException ex)
        {
            _readFailed = true;
            AppLogger.Error($"[PortDataStorageService] {ex.Message}");
            return new PortDatas();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PortDataStorageService] Load failed: {ex.Message}");
            return new PortDatas();
        }
    }

    public List<PortData> LoadPortData() => LoadPortDatas().portDatas;

    public void SavePortDatas(PortDatas container)
    {
        if (_readFailed)
        {
            AppLogger.Error($"[PortDataStorageService] 先前讀取失敗,拒絕寫入 PortDatas 以免覆蓋磁碟上完好的設定。請排除問題後重啟服務。");
            return;
        }
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
