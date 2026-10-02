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
        Assert.False(options.IsAllowAllOrigins);
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
            Assert.False(options.IsAllowAllOrigins);
            Assert.Equal(["https://portal.example.test"], options.AllowedOrigins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrayMode_ExplicitAllowAll_BypassesEmptyAllowlist()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {"agent":{"isAllowAllOrigins":true,"allowedOrigins":[]}}
                """);

            AgentRuntimeOptions options = AgentRuntimeOptions.Load([], path);

            Assert.True(options.IsOperational);
            Assert.True(options.IsAllowAllOrigins);
            Assert.Empty(options.AllowedOrigins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"agent\":{\"allowedOrigins\":[]}}")]
    [InlineData("{\"agent\":{}}")]
    public void TrayMode_LegacyEmptyConfiguration_InfersAllowAll(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);

            AgentRuntimeOptions options = AgentRuntimeOptions.Load([], path);

            Assert.True(options.IsOperational);
            Assert.True(options.IsAllowAllOrigins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TrayMode_ExplicitAllowlistMode_RequiresAtLeastOneOrigin()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {"agent":{"isAllowAllOrigins":false,"allowedOrigins":[]}}
                """);

            AgentRuntimeOptions options = AgentRuntimeOptions.Load([], path);

            Assert.False(options.IsOperational);
            Assert.Equal("origin_configuration_invalid", options.ConfigurationError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Provisioner_CopiesValidExampleOnceWithoutOverwritingTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-provision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "agentsettings.json");
        string example = Path.Combine(directory, "agentsettings.example.json");
        try
        {
            File.WriteAllText(example, """
                {"agent":{"isAllowAllOrigins":true,"allowedOrigins":[]}}
                """);

            Assert.True(AgentSettingsProvisioner.EnsureFromPackagedExample(target, example));
            string first = File.ReadAllText(target);
            File.WriteAllText(example, "invalid");

            Assert.True(AgentSettingsProvisioner.EnsureFromPackagedExample(target, example));
            Assert.Equal(first, File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(directory, true);
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
