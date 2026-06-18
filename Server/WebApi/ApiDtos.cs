using EdgeLink.Mask;
using EdgeLink.NetworkServer.Base.Models;

namespace EdgeLink.WebApi;

public class PortListResponse        { public List<PortDto> ports = new(); }
public class MaskListResponse        { public List<string> maskTypes = new(); }
public class LogListResponse         { public int total; public List<string> logs = new(); }
public class ClientDetailListResponse{ public List<TcpClientInfo> clients = new(); }
public class MonitorPortResponse     { public string protocolName = ""; }
public class SettingsExportDto       { public List<PortExportDto> ports = new(); public List<MaskDefinitionDto> masks = new(); }
public class ApiResult               { public bool success; public string? error; public string? id; }

public class PortDto
{
    public string id = "";
    public string protocolName = "";
    public string netProtocol = "";
    public string maskType = "";
    public string responseMaskType = "";
    public string requestMode = "";
    public string localPort = "";
    public string remotePort = "";
    public string targetIp = "";
    public bool isConnected;
    public bool isEnabled;
    public string sourceProtocolName = "";
    public string sourceProtocolId = "";
    public int currentConnections;
    public int totalConnections;
    public long totalReceivedBytes;
    public List<string> connectedDeviceIds = new();
    public ModbusConfig? modbus;
}

public class AddPortReq
{
    public string? protocolName;
    public string? netProtocol;
    public string? localPort;
    public string? remotePort;
    public string? targetIp;
    public string? maskType;
    public string? responseMaskType;
    public string? requestMode;
    public string? sourceProtocolName;
    public string? sourceProtocolId;
    public ModbusConfig? modbus;
}

public class UpdatePortReq : AddPortReq { }
public class DeletePortReq  { public string? id; }
public class ChangeMaskReq  { public string? maskType; }
public class ToggleEnabledReq { public bool enabled; }
public class RenameReq      { public string? newId; }
public class AddMaskReq     { public string? maskId; public string? localizationKey; }
public class MonitorPortReq { public string? id; }
public class LanguageReq    { public string? languageCode; }

public class MaskDefinitionDto
{
    public string maskId = "";
    public string localizationKey = "";
    public string description = "";
    public string fieldDelimiter = "";
    public string kvSeparator = "";
    public string outputTemplate = "";
    public string sampleData = "";
    public string routeMode = "";
    public string correlationIdField = "";
}

public class PortExportDto
{
    public string protocolName = "";
    public string netProtocol = "";
    public string localPort = "";
    public string remotePort = "";
    public string targetIp = "";
    public string maskType = "";
    public string responseMaskType = "";
    public string requestMode = "";
    public string sourceProtocolName = "";
    public string sourceProtocolId = "";
    public bool isEnabled = true;
    public ModbusConfig? modbus;
}
