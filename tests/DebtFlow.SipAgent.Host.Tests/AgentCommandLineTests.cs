using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class AgentCommandLineTests
{
    [Fact]
    public void ReleaseMetadata_UsesRuntimeProtocolStorageAndCapabilitySources()
    {
        AgentReleaseMetadata metadata = AgentReleaseMetadata.Current();

        Assert.Equal("1.0.0", metadata.Version);
        Assert.Equal(DebtFlow.SipAgent.Protocol.ProtocolConstants.Version, metadata.ProtocolVersion);
        Assert.Equal(DebtFlow.SipAgent.Persistence.SqliteAgentEventStore.CurrentSchemaVersion, metadata.SqliteSchemaVersion);
        Assert.Contains("call.mute", metadata.Capabilities);
        Assert.Contains("audio.output.volume", metadata.Capabilities);
        Assert.Contains("audio.input.volume", metadata.Capabilities);
        Assert.Contains("audio.volume.preferences", metadata.Capabilities);
        Assert.Contains("audio.devices", metadata.Capabilities);
        Assert.Contains("audio.output.test", metadata.Capabilities);
        Assert.Contains("audio.input.test", metadata.Capabilities);
    }

    [Fact]
    public void Parses_each_explicit_certificate_maintenance_command()
    {
        Assert.Equal(
            AgentLaunchCommand.RepairLocalCertificate,
            AgentCommandLine.Parse(["--repair-local-certificate"]));
        Assert.Equal(
            AgentLaunchCommand.RemoveLocalCertificate,
            AgentCommandLine.Parse(["--remove-local-certificate"]));
        Assert.Equal(
            AgentLaunchCommand.StoragePreflight,
            AgentCommandLine.Parse(["--storage-preflight"]));
        Assert.Equal(
            AgentLaunchCommand.PrintReleaseMetadata,
            AgentCommandLine.Parse(["--print-release-metadata"]));
    }

    [Theory]
    [InlineData("--repair-local-certificate", "--console")]
    [InlineData("--remove-local-certificate", "--background")]
    [InlineData("--repair-local-certificate", "--allowed-origin", "https://portal.example")]
    [InlineData("--repair-local-certificate", "--remove-local-certificate")]
    public void Rejects_maintenance_flags_combined_with_runtime_arguments(params string[] args)
    {
        AgentConfigurationException exception = Assert.Throws<AgentConfigurationException>(
            () => AgentCommandLine.Parse(args));
        Assert.Equal("maintenance_arguments_invalid", exception.Code);
    }
}
