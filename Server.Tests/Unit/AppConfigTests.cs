using EdgeLink.Infrastructure;
using Xunit;

namespace EdgeLink.Tests.Unit;

public class AppConfigTests
{
    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_WhenNoArgs_AreApplied()
    {
        var cfg = AppConfig.FromArgs([]);

        Assert.Equal(8081, cfg.HttpPort);
    }

    // ── CLI args ─────────────────────────────────────────────────────────────

    [Fact]
    public void Port_ParsedFromCli()
    {
        var cfg = AppConfig.FromArgs(["--port", "9090"]);
        Assert.Equal(9090, cfg.HttpPort);
    }

    [Fact]
    public void MultipleArgs_AllParsed()
    {
        var cfg = AppConfig.FromArgs(["--port", "7070", "--install"]);

        Assert.Equal(7070, cfg.HttpPort);
        Assert.True(cfg.InstallService);
    }

    // ── 已移除的 HTTPS 旗標 ──────────────────────────────────────────────────

    /// <summary>
    /// 舊的服務註冊(binPath)與啟動腳本裡還帶著 --https / --no-https / --https-port。
    /// 這些必須被安靜地忽略而不是讓程式失敗 —— 否則升級的既有部署會直接起不來。
    /// </summary>
    [Theory]
    [InlineData("--no-https")]
    [InlineData("--https")]
    public void RemovedHttpsFlags_AreIgnored_NotFatal(string flag)
    {
        var cfg = AppConfig.FromArgs(["--port", "7070", flag]);
        Assert.Equal(7070, cfg.HttpPort);
    }

    [Fact]
    public void RemovedHttpsPortFlag_DoesNotAffectHttpPort()
    {
        var cfg = AppConfig.FromArgs(["--https-port", "9443", "--port", "7070"]);
        Assert.Equal(7070, cfg.HttpPort);
    }

    [Fact]
    public void RemovedHttpsEnvVars_AreIgnored()
    {
        Environment.SetEnvironmentVariable("EDGELINK_HTTPS", "0");
        Environment.SetEnvironmentVariable("EDGELINK_HTTPS_PORT", "9999");
        try
        {
            var cfg = AppConfig.FromArgs([]);
            Assert.Equal(8081, cfg.HttpPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGELINK_HTTPS", null);
            Environment.SetEnvironmentVariable("EDGELINK_HTTPS_PORT", null);
        }
    }

    // ── Environment variables ────────────────────────────────────────────────

    [Fact]
    public void Port_FallsBackToEnvVar()
    {
        Environment.SetEnvironmentVariable("EDGELINK_PORT", "8181");
        try
        {
            var cfg = AppConfig.FromArgs([]);
            Assert.Equal(8181, cfg.HttpPort);
        }
        finally { Environment.SetEnvironmentVariable("EDGELINK_PORT", null); }
    }

    // ── Priority: CLI > env > default ────────────────────────────────────────

    [Fact]
    public void Cli_TakesPriorityOverEnv()
    {
        Environment.SetEnvironmentVariable("EDGELINK_PORT", "5555");
        try
        {
            var cfg = AppConfig.FromArgs(["--port", "6666"]);
            Assert.Equal(6666, cfg.HttpPort);
        }
        finally { Environment.SetEnvironmentVariable("EDGELINK_PORT", null); }
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void InvalidPortArg_UsesDefault()
    {
        var cfg = AppConfig.FromArgs(["--port", "notanumber"]);
        Assert.Equal(8081, cfg.HttpPort);
    }

    [Fact]
    public void MissingPortValue_UsesDefault()
    {
        var cfg = AppConfig.FromArgs(["--port"]);   // value missing
        Assert.Equal(8081, cfg.HttpPort);
    }
}
