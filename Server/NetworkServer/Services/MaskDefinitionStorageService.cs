using EdgeLink.Infrastructure;
using EdgeLink.Mask;

namespace EdgeLink.NetworkServer.Services;

public class MaskDefinitionStorageService
{

    /// <summary>
    /// 讀取失敗過就不再寫入。讀取失敗代表檔案內容仍然完好(只是當下拿不到),
    /// 此時記憶體裡是空的預設值,任何一次存檔都會把使用者的設定永久覆蓋掉。
    /// 需要重啟服務(且問題排除後)才會恢復寫入。
    /// </summary>
    private bool _readFailed;
    public MaskDefinitions Load()
    {
        try { return SettingLoader.Load<MaskDefinitions>() ?? new MaskDefinitions(); }
        catch (SettingReadException ex)
        {
            _readFailed = true;
            AppLogger.Error($"[MaskDefinitionStorageService] {ex.Message}");
            return new MaskDefinitions();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[MaskDefinitionStorageService] Load failed: {ex.Message}");
            return new MaskDefinitions();
        }
    }

    public void Save(MaskDefinitions data)
    {
        if (_readFailed)
        {
            AppLogger.Error($"[MaskDefinitionStorageService] 先前讀取失敗,拒絕寫入 MaskDefinitions 以免覆蓋磁碟上完好的設定。請排除問題後重啟服務。");
            return;
        }
        try { SettingLoader.Save(data); }
        catch (Exception ex)
        {
            AppLogger.Error($"[MaskDefinitionStorageService] Save failed: {ex.Message}");
        }
    }
}
