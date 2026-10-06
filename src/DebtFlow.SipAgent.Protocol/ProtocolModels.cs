using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebtFlow.SipAgent.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumMessagesPerWindow = 60;
    public static readonly TimeSpan MessageRateWindow = TimeSpan.FromSeconds(10);
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

public sealed record EmptyPayload;

public sealed record CommandHeader(string CommandId);

public sealed record ConfigureCommand(
    string CommandId,
    string Host,
    int Port,
    string Username,
    string Password,
    string? CollectionId = null,
    string? CollectionBindingId = null);

public sealed record CallStartCommand(
    string CommandId,
    string CallId,
    string Destination,
    string? ContextToken = null,
    string? CallContextId = null);

public sealed record CallCommand(string CommandId, string CallId);

public sealed record DtmfCommand(string CommandId, string CallId, string Digit);

public sealed record CallMuteCommand(string CommandId, string CallId, bool Muted);

public sealed record CallVolumeCommand(string CommandId, string CallId, int Volume);

public sealed record AudioVolumePreferenceCommand(string CommandId, int Volume);

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

public sealed record AudioControlsSnapshot(
    bool MicrophoneMuted,
    int OutputVolume,
    int InputVolume);

public sealed record AgentSnapshotPayload(
    string AgentState,
    string RegistrationState,
    string AudioState,
    IReadOnlyList<ActiveCallSnapshot> ActiveCalls,
    long PendingEventCount,
    long LastSequence,
    string? AgentStateCode = null,
    long? LastAcknowledgedSequence = null,
    string? OutboxCapacityState = null,
    long? OutboxStorageBytes = null,
    long? OldestPendingAgeSeconds = null,
    AudioControlsSnapshot? AudioControls = null);

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
    JsonElement Data,
    string? CollectionId = null,
    string? CollectionBindingId = null,
    string? CallContextId = null);

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
        RespectRequiredConstructorParameters = true,
        WriteIndented = false
    };
}
