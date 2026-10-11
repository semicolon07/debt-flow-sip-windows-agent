using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class AgentInstanceProtocolTests
{
    [Theory]
    [InlineData((byte)AgentInstanceCommand.Activate)]
    [InlineData((byte)AgentInstanceCommand.QueryMaintenance)]
    [InlineData((byte)AgentInstanceCommand.PrepareUpdate)]
    public void Request_round_trips(byte commandValue)
    {
        AgentInstanceCommand command = (AgentInstanceCommand)commandValue;
        byte[] encoded = AgentInstanceProtocol.EncodeRequest(command);

        Assert.True(AgentInstanceProtocol.TryDecodeRequest(encoded, out AgentInstanceCommand decoded));
        Assert.Equal(command, decoded);
    }

    [Theory]
    [InlineData(AgentInstanceResponseStatus.Ready)]
    [InlineData(AgentInstanceResponseStatus.ActiveCall)]
    [InlineData(AgentInstanceResponseStatus.ShutdownInProgress)]
    [InlineData(AgentInstanceResponseStatus.ShutdownFailed)]
    [InlineData(AgentInstanceResponseStatus.ProtocolUnsupported)]
    public void Response_round_trips(AgentInstanceResponseStatus status)
    {
        byte[] encoded = AgentInstanceProtocol.EncodeResponse(status);

        Assert.True(AgentInstanceProtocol.TryDecodeResponse(encoded, out AgentInstanceResponseStatus decoded));
        Assert.Equal(status, decoded);
    }

    [Theory]
    [InlineData(0, 1, 1, 0)]
    [InlineData(0x44, 2, 1, 0)]
    [InlineData(0x44, 1, 99, 0)]
    [InlineData(0x44, 1, 1, 1)]
    public void Invalid_request_is_rejected(byte magic, byte version, byte command, byte reserved)
    {
        Assert.False(AgentInstanceProtocol.TryDecodeRequest(
            [magic, version, command, reserved],
            out _));
    }

    [Theory]
    [InlineData(0, 1, 1, 0)]
    [InlineData(0x44, 2, 1, 0)]
    [InlineData(0x44, 1, 99, 0)]
    [InlineData(0x44, 1, 1, 1)]
    public void Invalid_response_is_rejected(byte magic, byte version, byte status, byte reserved)
    {
        Assert.False(AgentInstanceProtocol.TryDecodeResponse(
            [magic, version, status, reserved],
            out _));
    }

    [Fact]
    public void Update_exit_codes_are_stable()
    {
        Assert.Equal(30, AgentUpdateExitCodes.ActiveCall);
        Assert.Equal(31, AgentUpdateExitCodes.ShutdownFailed);
        Assert.Equal(32, AgentUpdateExitCodes.IpcUnavailable);
        Assert.Equal(33, AgentUpdateExitCodes.ProtocolUnsupported);
    }
}
