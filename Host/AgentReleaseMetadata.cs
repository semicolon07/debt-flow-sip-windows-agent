using System.Reflection;
using DebtFlow.SipAgent.Persistence;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed record AgentReleaseMetadata(
    int SchemaVersion,
    string Version,
    int ProtocolVersion,
    int SqliteSchemaVersion,
    string RuntimeIdentifier,
    IReadOnlyList<string> Capabilities)
{
    public const int ManifestSchemaVersion = 1;
    public const int StorageSchemaVersion = SqliteAgentEventStore.CurrentSchemaVersion;
    public const int LocalProtocolVersion = ProtocolConstants.Version;

    public static IReadOnlyList<string> CurrentCapabilities { get; } =
    [
        "sip.register",
        "call.outbound",
        "call.inbound",
        "call.dtmf",
        "call.mute",
        "audio.output.volume",
        "audio.input.volume",
        "event.durable",
        "event.collection_binding.v1"
    ];

    public static AgentReleaseMetadata Current()
    {
        Version? version = typeof(AgentReleaseMetadata).Assembly.GetName().Version;
        string productVersion = version == null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return new(
            ManifestSchemaVersion,
            productVersion,
            LocalProtocolVersion,
            StorageSchemaVersion,
            "win-x64",
            CurrentCapabilities);
    }
}
