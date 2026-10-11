namespace DebtFlow.SipAgent.Host;

internal enum AgentInstanceCommand : byte
{
    Activate = 1,
    QueryMaintenance = 2,
    PrepareUpdate = 3
}

public enum AgentInstanceResponseStatus : byte
{
    Ready = 1,
    ActiveCall = 2,
    ShutdownInProgress = 3,
    ShutdownFailed = 4,
    ProtocolUnsupported = 5
}

public sealed record AgentInstanceResponse(AgentInstanceResponseStatus Status);

public static class AgentUpdateExitCodes
{
    public const int ActiveCall = 30;
    public const int ShutdownFailed = 31;
    public const int IpcUnavailable = 32;
    public const int ProtocolUnsupported = 33;
}

internal static class AgentInstanceProtocol
{
    internal const byte LegacyActivateSignal = 1;
    internal const byte Magic = 0x44;
    internal const byte Version = 1;
    internal const int FrameLength = 4;

    internal static byte[] EncodeRequest(AgentInstanceCommand command) =>
        [Magic, Version, (byte)command, 0];

    internal static bool TryDecodeRequest(ReadOnlySpan<byte> frame, out AgentInstanceCommand command)
    {
        command = default;
        if (frame.Length != FrameLength || frame[0] != Magic || frame[1] != Version || frame[3] != 0)
        {
            return false;
        }

        command = (AgentInstanceCommand)frame[2];
        return Enum.IsDefined(command);
    }

    internal static byte[] EncodeResponse(AgentInstanceResponseStatus status) =>
        [Magic, Version, (byte)status, 0];

    internal static bool TryDecodeResponse(ReadOnlySpan<byte> frame, out AgentInstanceResponseStatus status)
    {
        status = default;
        if (frame.Length != FrameLength || frame[0] != Magic || frame[1] != Version || frame[3] != 0)
        {
            return false;
        }

        status = (AgentInstanceResponseStatus)frame[2];
        return Enum.IsDefined(status);
    }
}
