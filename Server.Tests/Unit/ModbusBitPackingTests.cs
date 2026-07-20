using EdgeLink.NetworkServer.Modbus;
using Xunit;

namespace Server.Tests.Unit;

/// <summary>
/// Read Coils / Discrete Inputs 會把 N 個 bit 打包成一個數值。先前用 int 累積且
/// 寫成 `v |= (1 &lt;&lt; i)` —— C# 對 int 的位移量會被遮罩成 5 bits,所以 i=32 時
/// `1 &lt;&lt; 32` 等於 1,第 32 個之後的 bit 會靜默折回低位。
/// Modbus 規格允許一次讀到 2000 個 coil,Quantity &gt;= 32 是合法設定。
/// </summary>
public class ModbusBitPackingTests
{
    private static byte[] BitsAt(params int[] indices)
    {
        var b = new byte[(indices.Max() / 8) + 1];
        foreach (int i in indices) b[i / 8] |= (byte)(1 << (i % 8));
        return b;
    }

    [Fact]
    public void Bit32_DoesNotWrapIntoBit0()
    {
        // 只有第 32 個 bit 是 1。修正前 `1 << 32` == 1 → 會輸出 "1"。
        string s = ModbusTcpMasterConnector.BoolsToString(BitsAt(32), 40);
        Assert.Equal((1UL << 32).ToString(), s);
        Assert.NotEqual("1", s);
    }

    [Fact]
    public void Bit31_IsNotNegative()
    {
        // 修正前 int 的第 31 位是符號位 → 輸出 -2147483648
        string s = ModbusTcpMasterConnector.BoolsToString(BitsAt(31), 32);
        Assert.Equal("2147483648", s);
        Assert.DoesNotContain("-", s);
    }

    [Fact]
    public void LowBits_StillBehaveAsBefore()
    {
        Assert.Equal("1", ModbusTcpMasterConnector.BoolsToString(BitsAt(0), 1));
        Assert.Equal("5", ModbusTcpMasterConnector.BoolsToString(BitsAt(0, 2), 8));
    }

    [Fact]
    public void BeyondSixtyFourBits_TruncatesInsteadOfCorrupting()
    {
        // 100 個 bit 放不進任何整數型別。重點是不能靜默折回低位。
        string s = ModbusTcpMasterConnector.BoolsToString(BitsAt(0, 70), 100);
        Assert.Equal("1", s);   // 第 70 位被捨棄(有記警告),而不是折回去變成別的值
    }
}
