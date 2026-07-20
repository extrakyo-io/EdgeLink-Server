using System;
using EdgeLink.NetworkServer.Modbus;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>#19 的迴歸測試。</summary>
public class ModbusLogThrottleTests
{
    /// <summary>先前的守衛是 `data.ConsecutiveFailures < 3`,但該計數器只在「連線層級」
    /// 例外時遞增 —— ModbusException(位址不存在)不屬於那幾種型別,所以條件恆真,
    /// 100ms 輪詢下每天約 86 萬行。</summary>
    [Fact]
    public void FirstError_IsLogged()
    {
        var data = new ModbusTcpMasterData();
        Assert.True(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "temp"));
    }

    [Fact]
    public void RepeatedErrorsWithinWindow_AreSuppressed()
    {
        var data = new ModbusTcpMasterData();
        var t0   = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "temp", t0));
        // 10Hz 輪詢:接下來 59 秒內的每一次都不該再記
        for (int i = 1; i <= 590; i++)
            Assert.False(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "temp", t0.AddSeconds(i * 0.1)));
    }

    [Fact]
    public void AfterWindow_LogsAgain()
    {
        var data = new ModbusTcpMasterData();
        var t0   = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "temp", t0));
        Assert.True(ModbusTcpMasterConnector.ShouldLogRegisterError(
            data, "temp", t0.AddSeconds(ModbusTcpMasterConnector.RegisterErrorLogIntervalSec + 1)));
    }

    /// <summary>限流是逐 register 的,一個位址寫錯不該把其他位址的錯誤也吃掉。</summary>
    [Fact]
    public void ThrottleIsPerRegister()
    {
        var data = new ModbusTcpMasterData();
        var t0   = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "temp",  t0));
        Assert.True(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "humid", t0));
        Assert.False(ModbusTcpMasterConnector.ShouldLogRegisterError(data, "temp", t0.AddSeconds(1)));
    }
}
