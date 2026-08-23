using System.Net;
using System.Text;
using EdgeLink.Infrastructure;
using EdgeLink.Mask;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Services;

namespace EdgeLink.WebApi;

public class SettingsApiHandler
{
    public Task ExportAsync(HttpListenerContext ctx)
    {
        var result = new SettingsExportDto();

        foreach (var p in PortManager.Instance.GetAllPortDatas())
        {
            result.ports.Add(new PortExportDto
            {
                protocolName       = p.ProtocolName,
                netProtocol        = p.NetProtocol,
                localPort          = p.LocalPortDetails?.Port  ?? "",
                remotePort         = p.RemotePortDetails?.Port ?? "",
                targetIp           = p.TargetIP    ?? "",
                maskType           = p.MaskType    ?? "OriginalData",
                responseMaskType   = p.ResponseMaskType ?? "",
                requestMode        = p.RequestMode ?? "serial",
                sourceProtocolName = p.SourceProtocolName ?? "",
                sourceProtocolId   = p.SourceProtocolId ?? "",
                isEnabled          = p.IsEnabled,
                modbus             = p.Modbus,
            });
        }

        foreach (var id in MaskDefinitionManager.Instance.GetMaskTypeIds())
        {
            if (id == "OriginalData") continue;
            var def = MaskDefinitionManager.Instance.GetDefinition(id);
            if (def == null) continue;
            result.masks.Add(new MaskDefinitionDto
            {
                maskId             = def.maskId,
                localizationKey    = def.localizationKey,
                description        = def.description,
                fieldDelimiter     = def.fieldDelimiter,
                kvSeparator        = def.kvSeparator,
                outputTemplate     = def.outputTemplate,
                sampleData         = def.sampleData,
                routeMode          = def.routeMode ?? "",
                correlationIdField = def.correlationIdField ?? "",
                binary             = def.binary,
            });
        }

        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(result));
        return Task.CompletedTask;
    }

    public async Task ImportAsync(HttpListenerContext ctx)
    {
        string? body = await HttpApiServer.ReadBodyAsync(ctx);
        if (body == null) return;   // 已回 413

        SettingsExportDto? dto;
        try { dto = Json.FromJson<SettingsExportDto>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON"); return; }
        if (dto == null) { HttpApiServer.WriteError(ctx, 400, "Invalid format"); return; }

        try
        {
            foreach (var mask in dto.masks ?? new List<MaskDefinitionDto>())
            {
                if (string.IsNullOrEmpty(mask.maskId)) continue;

                // 匯入的 binary spec 同樣要驗證,否則壞設定會直接進到執行中的埠
                string? specError = BinarySpecValidator.Validate(mask.binary);
                if (specError != null)
                {
                    HttpApiServer.WriteError(ctx, 400, $"mask '{mask.maskId}' 的 binary spec 無效:{specError}");
                    return;
                }

                if (!MaskDefinitionManager.Instance.HasMaskType(mask.maskId))
                    MaskDefinitionManager.Instance.AddMaskType(mask.maskId, mask.localizationKey);
                MaskDefinitionManager.Instance.SaveDefinition(new MaskDefinition
                {
                    maskId             = mask.maskId,
                    localizationKey    = mask.localizationKey,
                    description        = mask.description,
                    fieldDelimiter     = mask.fieldDelimiter,
                    kvSeparator        = mask.kvSeparator,
                    outputTemplate     = mask.outputTemplate,
                    sampleData         = mask.sampleData,
                    routeMode          = mask.routeMode ?? "",
                    correlationIdField = mask.correlationIdField ?? "",
                    binary             = mask.binary,
                });
            }

            foreach (var p in dto.ports ?? new List<PortExportDto>())
            {
                if (string.IsNullOrEmpty(p.protocolName) || string.IsNullOrEmpty(p.netProtocol)) continue;
                var portData = new PortData
                {
                    ProtocolName       = p.protocolName,
                    NetProtocol        = p.netProtocol,
                    LocalPortDetails   = new PortDetails { Port = string.IsNullOrEmpty(p.localPort)  ? "--" : p.localPort },
                    RemotePortDetails  = new PortDetails { Port = string.IsNullOrEmpty(p.remotePort) ? "--" : p.remotePort },
                    TargetIP           = p.targetIp ?? "",
                    MaskType           = string.IsNullOrEmpty(p.maskType) ? "OriginalData" : p.maskType,
                    ResponseMaskType   = p.responseMaskType ?? "",
                    RequestMode        = string.IsNullOrEmpty(p.requestMode) ? "serial" : p.requestMode,
                    SourceProtocolName = p.sourceProtocolName ?? "",
                    SourceProtocolId   = p.sourceProtocolId ?? "",
                    IsEnabled          = p.isEnabled,
                    IsConnected        = false,
                    Modbus             = p.modbus,
                };
                if (PortManager.Instance.IsPortUnique(portData))
                    PortManager.Instance.AddPortData(portData);
            }

            RelinkSourceProtocols();

            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (Exception ex) { HttpApiServer.WriteError(ctx, 500, ex.Message); }
    }

    /// <summary>
    /// 匯入後把 <c>SourceProtocolId</c> 依 <c>SourceProtocolName</c> 重新接回去。
    ///
    /// <para>埠的 Id 是在 <c>AddPortData</c> 當下用 Guid 產生的,而匯出檔裡帶的是「來源機器上」
    /// 的舊 Id —— 匯進另一台(或刪掉重建)之後那個 Id 根本不存在。Router 是純粹比對
    /// <c>SourceProtocolId</c> 來決定要轉發給誰的,所以連結一斷,埠看起來都在、狀態也正常,
    /// 資料卻靜靜地不會流動。這種故障沒有錯誤訊息可看,只能靠這裡自動接回。</para>
    ///
    /// <para>只修「指到不存在的 Id」的那些;已經對得上的不動,同名找不到的也不動
    /// (使用者可能是刻意留白,或來源埠這次沒一起匯入)。</para>
    /// </summary>
    private static void RelinkSourceProtocols()
    {
        var ports = PortManager.Instance.GetAllPortDatas();
        var idByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in ports)
        {
            knownIds.Add(p.Id);
            if (!string.IsNullOrEmpty(p.ProtocolName)) idByName[p.ProtocolName] = p.Id;
        }

        bool changed = false;
        foreach (var p in ports)
        {
            if (string.IsNullOrEmpty(p.SourceProtocolName)) continue;
            if (!string.IsNullOrEmpty(p.SourceProtocolId) && knownIds.Contains(p.SourceProtocolId)) continue;

            if (idByName.TryGetValue(p.SourceProtocolName, out var id) && id != p.SourceProtocolId)
            {
                AppLogger.Log($"[Settings] 匯入後重新接上來源埠:{p.ProtocolName} → " +
                              $"{p.SourceProtocolName} ({p.SourceProtocolId} → {id})");
                p.SourceProtocolId = id;
                changed = true;
            }
        }

        if (changed) PortManager.Instance.SaveData();
    }
}
