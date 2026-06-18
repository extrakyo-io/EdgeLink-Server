using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EdgeLink.NetworkServer.Base.Models;

[Serializable]
public class PortDatas
{
    public List<PortData> portDatas = new();
}

[Serializable]
public class PortData
{
    public string Id = "";
    public string Key = "";
    public string ProtocolName = "";
    public string NetProtocol = "";
    public PortDetails LocalPortDetails = new();
    public PortDetails RemotePortDetails = new();
    public string TargetIP = "";
    public bool IsConnected;
    public int COMReceived;
    public int NetReceived;
    public bool IsEnabled = true;
    public string MaskType = "";
    public string ResponseMaskType = "";
    public string RequestMode = "";
    public string SourceProtocolName = "";
    public string SourceProtocolId = "";
    public ModbusConfig? Modbus;

    [JsonIgnore] public int CurrentConnections;
    [JsonIgnore] public int TotalConnections;
    [JsonIgnore] public long TotalReceivedBytes;
    [JsonIgnore] public Action<PortData>? OnUpdate;
}

[Serializable]
public class PortDetails
{
    public string Port = "";
    public string Description = "";
}

// Modbus TCP Master polling 配置
[Serializable]
public class ModbusConfig
{
    public int SlaveId = 1;
    public int PollIntervalMs = 100;       // 10 Hz default
    public int ConnectTimeoutMs = 1000;
    public int ReadTimeoutMs = 1000;
    public string DeviceId = "";           // 變成 id:xxx 欄位的值
    public List<ModbusRegisterMap> Registers = new();
}

[Serializable]
public class ModbusRegisterMap
{
    public string Name = "";               // 欄位名 (yaw, pitch, jx, jy ...)
    public int FunctionCode = 3;           // 1=Coil, 2=DI, 3=Holding, 4=Input
    public int StartAddress;
    public int Quantity = 1;
    public string DataType = "uint16";     // bit | uint16 | int16 | uint32 | int32 | float32 | bits-int (GrayCode 8-16 bits 組合)
    public double Scale = 1.0;
    public double Offset;
}
