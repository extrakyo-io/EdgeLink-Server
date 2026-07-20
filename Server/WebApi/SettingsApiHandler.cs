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
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = await sr.ReadToEndAsync();

        SettingsExportDto? dto;
        try { dto = Json.FromJson<SettingsExportDto>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON"); return; }
        if (dto == null) { HttpApiServer.WriteError(ctx, 400, "Invalid format"); return; }

        try
        {
            foreach (var mask in dto.masks ?? new List<MaskDefinitionDto>())
            {
                if (string.IsNullOrEmpty(mask.maskId)) continue;
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

            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (Exception ex) { HttpApiServer.WriteError(ctx, 500, ex.Message); }
    }
}
