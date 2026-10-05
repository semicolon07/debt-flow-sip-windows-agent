using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class AgentCommandLineTests
{
    [Fact]
    public void Parses_each_explicit_certificate_maintenance_command()
    {
        Assert.Equal(
            AgentLaunchCommand.RepairLocalCertificate,
            AgentCommandLine.Parse(["--repair-local-certificate"]));
        Assert.Equal(
            AgentLaunchCommand.RemoveLocalCertificate,
            AgentCommandLine.Parse(["--remove-local-certificate"]));
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
