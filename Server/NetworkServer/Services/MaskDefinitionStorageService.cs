using EdgeLink.Infrastructure;
using EdgeLink.Mask;

namespace EdgeLink.NetworkServer.Services;

public class MaskDefinitionStorageService
{
    public MaskDefinitions Load()
    {
        try { return SettingLoader.Load<MaskDefinitions>() ?? new MaskDefinitions(); }
        catch (Exception ex)
        {
            AppLogger.Error($"[MaskDefinitionStorageService] Load failed: {ex.Message}");
            return new MaskDefinitions();
        }
    }

    public void Save(MaskDefinitions data)
    {
        try { SettingLoader.Save(data); }
        catch (Exception ex)
        {
            AppLogger.Error($"[MaskDefinitionStorageService] Save failed: {ex.Message}");
        }
    }
}
