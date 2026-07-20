using System.Net;
using System.Text;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Services;

namespace EdgeLink.WebApi;

public class PortApiHandler
{
    public Task GetAllAsync(HttpListenerContext ctx)
    {
        var list = PortManager.Instance.GetAllPortDatas()
            .Select(p => new PortDto
            {
                id                 = p.Id ?? "",
                protocolName       = p.ProtocolName,
                netProtocol        = p.NetProtocol,
                maskType           = p.MaskType ?? "",
                responseMaskType   = p.ResponseMaskType ?? "",
                requestMode        = p.RequestMode ?? "serial",
                localPort          = p.LocalPortDetails?.Port ?? "",
                remotePort         = p.RemotePortDetails?.Port ?? "",
                targetIp           = p.TargetIP ?? "",
                isConnected        = p.IsConnected,
                isEnabled          = p.IsEnabled,
                sourceProtocolName = p.SourceProtocolName ?? "",
                sourceProtocolId   = p.SourceProtocolId ?? "",
                currentConnections = p.CurrentConnections,
                totalConnections   = p.TotalConnections,
                totalReceivedBytes = p.TotalReceivedBytes,
                connectedDeviceIds = GetDeviceIds(p),
                modbus             = p.Modbus,
            }).ToList();
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new PortListResponse { ports = list }));
        return Task.CompletedTask;
    }

    public async Task AddAsync(HttpListenerContext ctx)
    {
        string? body = await HttpApiServer.ReadBodyAsync(ctx);
        if (body == null) return;   // 已回 413

        AddPortReq? req;
        try { req = Json.FromJson<AddPortReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (string.IsNullOrWhiteSpace(req?.protocolName) || string.IsNullOrWhiteSpace(req.netProtocol))
        {
            HttpApiServer.WriteError(ctx, 400, "protocolName and netProtocol are required");
            return;
        }

        try
        {
            string srcId   = req.sourceProtocolId ?? "";
            string srcName = req.sourceProtocolName ?? "";
            if (!string.IsNullOrEmpty(srcId) && string.IsNullOrEmpty(srcName))
            {
                var srcPort = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == srcId);
                if (srcPort != null) srcName = srcPort.ProtocolName;
            }

            var portData = new PortData
            {
                ProtocolName       = req.protocolName,
                NetProtocol        = req.netProtocol,
                LocalPortDetails   = new PortDetails { Port = string.IsNullOrEmpty(req.localPort)  ? "--" : req.localPort },
                RemotePortDetails  = new PortDetails { Port = string.IsNullOrEmpty(req.remotePort) ? "--" : req.remotePort },
                TargetIP           = req.targetIp ?? "",
                MaskType           = string.IsNullOrEmpty(req.maskType) ? "OriginalData" : req.maskType,
                ResponseMaskType   = req.responseMaskType ?? "",
                RequestMode        = string.IsNullOrEmpty(req.requestMode) ? "serial" : req.requestMode,
                SourceProtocolName = srcName,
                SourceProtocolId   = srcId,
                IsConnected        = false,
                Modbus             = req.modbus,
            };

            if (!PortManager.Instance.IsPortUnique(portData))
                throw new InvalidOperationException("Port already exists (same protocol + port number)");

            PortManager.Instance.AddPortData(portData);
            HttpApiServer.WriteJson(ctx, 201, Json.ToJson(new ApiResult { success = true, id = portData.Id }));
        }
        catch (InvalidOperationException ex) { HttpApiServer.WriteError(ctx, 409, ex.Message); }
        catch (ArgumentException ex)         { HttpApiServer.WriteError(ctx, 400, ex.Message); }
        catch (Exception ex)                 { HttpApiServer.WriteError(ctx, 500, ex.Message); }
    }

    public async Task DeleteAsync(HttpListenerContext ctx)
    {
        string? body = await HttpApiServer.ReadBodyAsync(ctx);
        if (body == null) return;   // 已回 413

        DeletePortReq? req;
        try { req = Json.FromJson<DeletePortReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        try
        {
            var port = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == req?.id);
            if (port == null) throw new KeyNotFoundException($"Port '{req?.id}' not found");
            await PortManager.Instance.RemovePortData(port);
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex) { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (Exception ex)            { HttpApiServer.WriteError(ctx, 500, ex.Message); }
    }

    public async Task UpdateAsync(HttpListenerContext ctx, string id)
    {
        string? body = await HttpApiServer.ReadBodyAsync(ctx);
        if (body == null) return;   // 已回 413

        UpdatePortReq? req;
        try { req = Json.FromJson<UpdatePortReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (string.IsNullOrWhiteSpace(req?.protocolName) || string.IsNullOrWhiteSpace(req.netProtocol))
        {
            HttpApiServer.WriteError(ctx, 400, "protocolName and netProtocol are required");
            return;
        }

        try
        {
            var old = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == id);
            if (old == null) throw new KeyNotFoundException($"Port '{id}' not found");

            string srcId   = req.sourceProtocolId ?? "";
            string srcName = req.sourceProtocolName ?? "";
            if (!string.IsNullOrEmpty(srcId) && string.IsNullOrEmpty(srcName))
            {
                var srcPort = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == srcId);
                if (srcPort != null) srcName = srcPort.ProtocolName;
            }

            var updated = new PortData
            {
                ProtocolName       = req.protocolName,
                NetProtocol        = req.netProtocol,
                LocalPortDetails   = new PortDetails { Port = string.IsNullOrEmpty(req.localPort)  ? "--" : req.localPort },
                RemotePortDetails  = new PortDetails { Port = string.IsNullOrEmpty(req.remotePort) ? "--" : req.remotePort },
                TargetIP           = req.targetIp ?? "",
                MaskType           = string.IsNullOrEmpty(req.maskType) ? "OriginalData" : req.maskType,
                ResponseMaskType   = req.responseMaskType ?? "",
                RequestMode        = string.IsNullOrEmpty(req.requestMode) ? "serial" : req.requestMode,
                SourceProtocolName = srcName,
                SourceProtocolId   = srcId,
                Modbus             = req.modbus,
            };

            await PortManager.Instance.UpdatePortData(old, updated);
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex) { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (Exception ex)            { HttpApiServer.WriteError(ctx, 500, ex.Message); }
    }

    public async Task ChangeMaskAsync(HttpListenerContext ctx, string id)
    {
        string? body = await HttpApiServer.ReadBodyAsync(ctx);
        if (body == null) return;   // 已回 413

        ChangeMaskReq? req;
        try { req = Json.FromJson<ChangeMaskReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        if (string.IsNullOrWhiteSpace(req?.maskType))
        {
            HttpApiServer.WriteError(ctx, 400, "maskType is required");
            return;
        }

        try
        {
            var port = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == id);
            if (port == null) throw new KeyNotFoundException($"Port '{id}' not found");
            port.MaskType = req.maskType;
            PortManager.Instance.SaveData();
            await PortManager.Instance.MaskSwitch(port);
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex)     { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (InvalidOperationException ex) { HttpApiServer.WriteError(ctx, 403, ex.Message); }
        catch (Exception ex)                 { HttpApiServer.WriteError(ctx, 400, ex.Message); }
    }

    public Task GetClientsAsync(HttpListenerContext ctx, string id)
    {
        var port = PortManager.Instance.GetAllPortDatas().FirstOrDefault(p => p.Id == id);
        if (port == null) { HttpApiServer.WriteError(ctx, 404, "Port not found"); return Task.CompletedTask; }

        List<TcpClientInfo> clients;
        if (port.NetProtocol.Contains("TCP SERVER", StringComparison.OrdinalIgnoreCase))
            clients = PortManager.Instance.ConnectorCore.GetTcpServerClients(port.Key);
        else if (port.NetProtocol.Contains("UDP", StringComparison.OrdinalIgnoreCase))
            clients = PortManager.Instance.ConnectorCore.GetUdpDevices(port.Key);
        else
        {
            HttpApiServer.WriteError(ctx, 400, "Only TCP Server and UDP support client listing");
            return Task.CompletedTask;
        }
        HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ClientDetailListResponse { clients = clients }));
        return Task.CompletedTask;
    }

    private static List<string> GetDeviceIds(PortData port)
    {
        if (port.NetProtocol.Contains("TCP SERVER", StringComparison.OrdinalIgnoreCase))
            return PortManager.Instance.ConnectorCore.GetTcpServerClients(port.Key)
                .Where(c => !string.IsNullOrEmpty(c.deviceId))
                .Select(c => c.deviceId).ToList();
        if (port.NetProtocol.Contains("UDP", StringComparison.OrdinalIgnoreCase))
            return PortManager.Instance.ConnectorCore.GetUdpDevices(port.Key)
                .Where(c => !string.IsNullOrEmpty(c.deviceId))
                .Select(c => c.deviceId).ToList();
        return new List<string>();
    }

    public async Task ToggleEnabledAsync(HttpListenerContext ctx, string id)
    {
        string? body = await HttpApiServer.ReadBodyAsync(ctx);
        if (body == null) return;   // 已回 413

        ToggleEnabledReq? req;
        try { req = Json.FromJson<ToggleEnabledReq>(body); }
        catch { HttpApiServer.WriteError(ctx, 400, "Invalid JSON body"); return; }

        try
        {
            await PortManager.Instance.TogglePortEnabled(id, req?.enabled ?? false);
            HttpApiServer.WriteJson(ctx, 200, Json.ToJson(new ApiResult { success = true }));
        }
        catch (KeyNotFoundException ex) { HttpApiServer.WriteError(ctx, 404, ex.Message); }
        catch (Exception ex)            { HttpApiServer.WriteError(ctx, 500, ex.Message); }
    }
}
