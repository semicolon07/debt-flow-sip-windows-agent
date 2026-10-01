using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class AgentRuntimeOptionsTests
{
    [Fact]
    public void DefaultConfigurationPath_UsesPerUserAgentStorageDirectory()
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DebtFlow",
            "SipAgent");

        Assert.Equal(Path.Combine(expectedRoot, "agentsettings.json"), AgentRuntimeOptions.GetConfigurationPath());
        Assert.Equal(expectedRoot, AgentStoragePaths.RootDirectory);
        Assert.Equal(expectedRoot, Path.GetDirectoryName(AgentStoragePaths.DatabasePath));
        Assert.Equal(expectedRoot, Path.GetDirectoryName(AgentStoragePaths.LogDirectory));
    }

    [Fact]
    public void ConsoleMode_EnablesOnlyDevelopmentOriginsByDefault()
    {
        AgentRuntimeOptions options = AgentRuntimeOptions.Load(["--console"]);

        Assert.True(options.ConsoleMode);
        Assert.True(options.IsOperational);
        Assert.Equal(
            ["http://127.0.0.1:8765", "http://localhost:8765"],
            options.AllowedOrigins.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConsoleMode_NormalizesAdditionalExactOrigin()
    {
        AgentRuntimeOptions options = AgentRuntimeOptions.Load(
            ["--console", "--allowed-origin", "https://portal.example.test/"]);

        Assert.Contains("https://portal.example.test", options.AllowedOrigins);
    }

    [Fact]
    public void TrayMode_RejectsCommandLineOriginOverride()
    {
        AgentConfigurationException exception = Assert.Throws<AgentConfigurationException>(
            () => AgentRuntimeOptions.Load(["--allowed-origin", "https://portal.example.test"]));

        Assert.Equal("allowed_origin_requires_console", exception.Code);
    }

    [Fact]
    public void UnknownArgument_IsRejected()
    {
        AgentConfigurationException exception = Assert.Throws<AgentConfigurationException>(
            () => AgentRuntimeOptions.Load(["--console", "--unknown"]));

        Assert.Equal("unsupported_argument", exception.Code);
    }

    [Fact]
    public void TrayMode_ReadsStrictProductionOriginConfiguration()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {"agent":{"allowedOrigins":["https://portal.example.test/"]}}
                """);

            AgentRuntimeOptions options = AgentRuntimeOptions.Load(["--background"], path);

            Assert.True(options.IsOperational);
            Assert.Equal(["https://portal.example.test"], options.AllowedOrigins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrayMode_FailsClosedForUnknownConfigurationField()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {"agent":{"allowedOrigins":["https://portal.example.test"],"unexpected":true}}
                """);

            AgentRuntimeOptions options = AgentRuntimeOptions.Load([], path);

            Assert.False(options.IsOperational);
            Assert.Empty(options.AllowedOrigins);
            Assert.Equal("origin_configuration_invalid", options.ConfigurationError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrayMode_DiscardsPreviouslyParsedOriginsWhenAnyOriginIsInvalid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {"agent":{"allowedOrigins":["https://portal.example.test","https://portal.example.test/path"]}}
                """);

            AgentRuntimeOptions options = AgentRuntimeOptions.Load([], path);

            Assert.False(options.IsOperational);
            Assert.Empty(options.AllowedOrigins);
            Assert.Equal("origin_configuration_invalid", options.ConfigurationError);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
