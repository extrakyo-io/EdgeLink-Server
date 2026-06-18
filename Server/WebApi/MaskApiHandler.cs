using System.Net;
using System.Text;
using EdgeLink.Infrastructure;
using EdgeLink.Mask;
using EdgeLink.NetworkServer.Services;

namespace EdgeLink.WebApi;

public class MaskApiHandler
{
    public Task GetAllAsync(HttpListenerContext ctx)
    {
        var ids = MaskDefinitionManager.Instance.GetMaskTypeIds();
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new MaskListResponse { maskTypes = ids }));
        return Task.CompletedTask;
    }

    public Task GetDefinitionAsync(HttpListenerContext ctx, string maskId)
    {
        var def = MaskDefinitionManager.Instance.GetDefinition(maskId);
        if (def == null) { HttpApiServer.WriteError(ctx, 404, $"MaskType '{maskId}' not found"); return Task.CompletedTask; }
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(ToDto(def)));
        return Task.CompletedTask;
    }

    public async Task SaveDefinitionAsync(HttpListenerContext ctx, string maskId)
    {
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = await sr.ReadToEndAsync();

        MaskDefinitionDto? dto;
        try { dto = Json.FromJson<MaskDefinitionDto>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (dto == null || string.IsNullOrWhiteSpace(dto.maskId))
        {
            HttpApiServer.WriteError(ctx, 400, "maskId is required");
            return;
        }
        if (dto.maskId != maskId) { HttpApiServer.WriteError(ctx, 400, "maskId in body must match URL"); return; }

        try
        {
            MaskDefinitionManager.Instance.SaveDefinition(FromDto(dto));
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (Exception ex) { HttpApiServer.WriteError(ctx, 400, ex.Message); }
    }

    public async Task RenameAsync(HttpListenerContext ctx, string maskId)
    {
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = await sr.ReadToEndAsync();

        RenameReq? req;
        try { req = Json.FromJson<RenameReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (string.IsNullOrWhiteSpace(req?.newId)) { HttpApiServer.WriteError(ctx, 400, "newId is required"); return; }

        try
        {
            bool anyUpdated = false;
            foreach (var port in PortManager.Instance.GetAllPortDatas())
            {
                if (port.MaskType == maskId) { port.MaskType = req.newId; anyUpdated = true; }
            }
            MaskDefinitionManager.Instance.RenameMask(maskId, req.newId!);
            if (anyUpdated) PortManager.Instance.SaveData();
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex) { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (Exception ex)            { HttpApiServer.WriteError(ctx, 400, ex.Message); }
    }

    public async Task AddAsync(HttpListenerContext ctx)
    {
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = await sr.ReadToEndAsync();

        AddMaskReq? req;
        try { req = Json.FromJson<AddMaskReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (string.IsNullOrWhiteSpace(req?.maskId)) { HttpApiServer.WriteError(ctx, 400, "maskId is required"); return; }

        try
        {
            if (MaskDefinitionManager.Instance.HasMaskType(req.maskId!))
                throw new InvalidOperationException($"MaskType '{req.maskId}' already exists");
            MaskDefinitionManager.Instance.AddMaskType(req.maskId!, req.localizationKey);
            HttpApiServer.WriteJson(ctx, 201, Json.ToJson(new ApiResult { success = true }));
        }
        catch (Exception ex) { HttpApiServer.WriteError(ctx, 400, ex.Message); }
    }

    public Task DeleteAsync(HttpListenerContext ctx, string maskId)
    {
        try
        {
            if (!MaskDefinitionManager.Instance.HasMaskType(maskId))
                throw new KeyNotFoundException($"MaskType '{maskId}' not found");
            MaskDefinitionManager.Instance.RemoveMaskType(maskId);
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex) { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (Exception ex)            { HttpApiServer.WriteError(ctx, 400, ex.Message); }
        return Task.CompletedTask;
    }

    private static MaskDefinitionDto ToDto(MaskDefinition def) => new MaskDefinitionDto
    {
        maskId = def.maskId, localizationKey = def.localizationKey, description = def.description,
        fieldDelimiter = def.fieldDelimiter, kvSeparator = def.kvSeparator, outputTemplate = def.outputTemplate,
        sampleData = def.sampleData, routeMode = def.routeMode ?? "", correlationIdField = def.correlationIdField ?? ""
    };

    private static MaskDefinition FromDto(MaskDefinitionDto dto) => new MaskDefinition
    {
        maskId = dto.maskId, localizationKey = dto.localizationKey, description = dto.description,
        fieldDelimiter = dto.fieldDelimiter, kvSeparator = dto.kvSeparator, outputTemplate = dto.outputTemplate,
        sampleData = dto.sampleData, routeMode = dto.routeMode, correlationIdField = dto.correlationIdField
    };
}
