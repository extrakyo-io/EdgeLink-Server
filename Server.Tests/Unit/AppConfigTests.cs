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
        Assert.True(cfg.HttpsEnabled);       // default is true after our change
        Assert.Equal(8443, cfg.HttpsPort);
    }

    // ── CLI args ─────────────────────────────────────────────────────────────

    [Fact]
    public void Port_ParsedFromCli()
    {
        var cfg = AppConfig.FromArgs(["--port", "9090"]);
        Assert.Equal(9090, cfg.HttpPort);
    }

    [Fact]
    public void HttpsPort_ParsedFromCli()
    {
        var cfg = AppConfig.FromArgs(["--https-port", "9443"]);
        Assert.Equal(9443, cfg.HttpsPort);
    }

    [Fact]
    public void NoHttps_Flag_DisablesHttps()
    {
        var cfg = AppConfig.FromArgs(["--no-https"]);
        Assert.False(cfg.HttpsEnabled);
    }

    [Fact]
    public void MultipleArgs_AllParsed()
    {
        var cfg = AppConfig.FromArgs(["--port", "7070", "--https-port", "7443", "--no-https"]);

        Assert.Equal(7070,  cfg.HttpPort);
        Assert.Equal(7443,  cfg.HttpsPort);
        Assert.False(cfg.HttpsEnabled);
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

    [Fact]
    public void HttpsPort_FallsBackToEnvVar()
    {
        Environment.SetEnvironmentVariable("EDGELINK_HTTPS_PORT", "9999");
        try
        {
            var cfg = AppConfig.FromArgs([]);
            Assert.Equal(9999, cfg.HttpsPort);
        }
        finally { Environment.SetEnvironmentVariable("EDGELINK_HTTPS_PORT", null); }
    }

    [Fact]
    public void HttpsDisabled_ByEnvVar()
    {
        Environment.SetEnvironmentVariable("EDGELINK_HTTPS", "0");
        try
        {
            var cfg = AppConfig.FromArgs([]);
            Assert.False(cfg.HttpsEnabled);
        }
        finally { Environment.SetEnvironmentVariable("EDGELINK_HTTPS", null); }
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
