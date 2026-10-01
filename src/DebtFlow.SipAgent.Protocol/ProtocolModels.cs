using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebtFlow.SipAgent.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(45);
}

public sealed record ProtocolEnvelope(
    int ProtocolVersion,
    string Kind,
    string MessageId,
    DateTimeOffset SentAtUtc,
    string Type,
    JsonElement Payload);

public sealed record HelloPayload(string PortalVersion, IReadOnlyList<int> SupportedProtocolVersions);

public sealed record CommandHeader(string CommandId);

public sealed record ConfigureCommand(
    string CommandId,
    string Host,
    int Port,
    string Username,
    string Password);

public sealed record CallStartCommand(
    string CommandId,
    string CallId,
    string Destination,
    string ContextToken);

public sealed record CallCommand(string CommandId, string CallId);

public sealed record DtmfCommand(string CommandId, string CallId, string Digit);

public sealed record AckPayload(string AgentInstanceId, long AcknowledgedThroughSequence);

public sealed record WelcomePayload(
    string AgentVersion,
    string AgentInstanceId,
    string AgentSessionId,
    IReadOnlyList<string> Capabilities,
    long LastSequence,
    long LastAcknowledgedSequence);

public sealed record CommandResultPayload(string CommandId, bool Accepted, string? ErrorCode);

public sealed record ErrorPayload(string? CorrelationMessageId, string Code, bool Retryable);

public sealed record ActiveCallSnapshot(
    string CallId,
    string Direction,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? AnsweredAtUtc);

public sealed record AgentSnapshotPayload(
    string AgentState,
    string RegistrationState,
    string AudioState,
    IReadOnlyList<ActiveCallSnapshot> ActiveCalls,
    long PendingEventCount,
    long LastSequence);

public sealed record DurableEventPayload(
    string Delivery,
    string EventId,
    string AgentInstanceId,
    string AgentSessionId,
    long Sequence,
    string CallId,
    string? CommandId,
    DateTimeOffset OccurredAtUtc,
    string? State,
    JsonElement Data);

public sealed record RealtimeEventPayload(
    string Delivery,
    string EventId,
    string AgentInstanceId,
    string AgentSessionId,
    DateTimeOffset OccurredAtUtc,
    JsonElement Data);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };
}
