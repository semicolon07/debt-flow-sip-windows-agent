using System.Text;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class ProtocolCodecTests
{
    [Fact]
    public void Deserialize_ValidEnvelope_RoundTripsRequiredFields()
    {
        string id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        byte[] json = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":1,\"kind\":\"hello\",\"messageId\":\"{id}\",\"sentAtUtc\":\"2026-10-01T00:00:00Z\",\"type\":\"session.hello\",\"payload\":{{\"portalVersion\":\"test\",\"supportedProtocolVersions\":[1]}}}}");

        ProtocolEnvelope envelope = ProtocolCodec.Deserialize(json);

        Assert.Equal(1, envelope.ProtocolVersion);
        Assert.Equal("hello", envelope.Kind);
        Assert.Equal(id, envelope.MessageId);
        HelloPayload payload = ProtocolCodec.DeserializePayload<HelloPayload>(envelope.Payload);
        Assert.Contains(1, payload.SupportedProtocolVersions);
    }

    [Fact]
    public void Deserialize_UnknownEnvelopeField_IsRejected()
    {
        string id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        byte[] json = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":1,\"kind\":\"hello\",\"messageId\":\"{id}\",\"sentAtUtc\":\"2026-10-01T00:00:00Z\",\"type\":\"session.hello\",\"payload\":{{}},\"extra\":true}}");

        ProtocolException exception = Assert.Throws<ProtocolException>(() => ProtocolCodec.Deserialize(json));

        Assert.Equal("invalid_message", exception.Code);
    }

    [Fact]
    public void Deserialize_UnsupportedVersion_IsRejected()
    {
        string id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        byte[] json = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":2,\"kind\":\"hello\",\"messageId\":\"{id}\",\"sentAtUtc\":\"2026-10-01T00:00:00Z\",\"type\":\"session.hello\",\"payload\":{{}}}}");

        ProtocolException exception = Assert.Throws<ProtocolException>(() => ProtocolCodec.Deserialize(json));

        Assert.Equal("protocol_version_unsupported", exception.Code);
    }

    [Fact]
    public void Deserialize_OversizedMessage_IsRejected()
    {
        byte[] bytes = new byte[ProtocolConstants.MaximumMessageBytes + 1];
        ProtocolException exception = Assert.Throws<ProtocolException>(() => ProtocolCodec.Deserialize(bytes));
        Assert.Equal("message_too_large", exception.Code);
    }

    [Fact]
    public void DeserializePayload_UnknownCommandField_IsRejected()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"commandId":"00000000-0000-0000-0000-000000000001","extra":true}""");

        ProtocolException exception = Assert.Throws<ProtocolException>(
            () => ProtocolCodec.DeserializePayload<CommandHeader>(document.RootElement));

        Assert.Equal("invalid_message", exception.Code);
    }

    [Fact]
    public void DeserializePayload_PingWithUnknownField_IsRejected()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"unexpected":true}""");

        ProtocolException exception = Assert.Throws<ProtocolException>(
            () => ProtocolCodec.DeserializePayload<EmptyPayload>(document.RootElement));

        Assert.Equal("invalid_message", exception.Code);
    }

    [Fact]
    public void DeserializePayload_MissingRequiredConstructorParameter_IsRejected()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");

        ProtocolException exception = Assert.Throws<ProtocolException>(
            () => ProtocolCodec.DeserializePayload<CommandHeader>(document.RootElement));

        Assert.Equal("invalid_message", exception.Code);
    }

    [Fact]
    public void Deserialize_DuplicateEnvelopeOrNestedPayloadField_IsRejected()
    {
        string id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string[] messages =
        [
            $"{{\"protocolVersion\":1,\"kind\":\"hello\",\"kind\":\"command\",\"messageId\":\"{id}\",\"sentAtUtc\":\"2026-10-01T00:00:00Z\",\"type\":\"session.hello\",\"payload\":{{}}}}",
            $"{{\"protocolVersion\":1,\"kind\":\"hello\",\"messageId\":\"{id}\",\"sentAtUtc\":\"2026-10-01T00:00:00Z\",\"type\":\"session.hello\",\"payload\":{{\"portalVersion\":\"first\",\"portalVersion\":\"second\",\"supportedProtocolVersions\":[1]}}}}"
        ];

        foreach (string message in messages)
        {
            ProtocolException exception = Assert.Throws<ProtocolException>(
                () => ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(message)));
            Assert.Equal("invalid_message", exception.Code);
        }
    }

    [Fact]
    public void Deserialize_NonIsoTimestamp_IsRejected()
    {
        string id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        byte[] json = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":1,\"kind\":\"hello\",\"messageId\":\"{id}\",\"sentAtUtc\":\"10/01/2026 00:00:00\",\"type\":\"session.hello\",\"payload\":{{}}}}");

        ProtocolException exception = Assert.Throws<ProtocolException>(() => ProtocolCodec.Deserialize(json));

        Assert.Equal("invalid_message", exception.Code);
    }

    [Fact]
    public void Deserialize_UppercaseUuid_IsRejected()
    {
        string id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        byte[] json = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":1,\"kind\":\"hello\",\"messageId\":\"{id}\",\"sentAtUtc\":\"2026-10-01T00:00:00Z\",\"type\":\"session.hello\",\"payload\":{{}}}}");

        ProtocolException exception = Assert.Throws<ProtocolException>(() => ProtocolCodec.Deserialize(json));

        Assert.Equal("invalid_message", exception.Code);
    }

    [Theory]
    [InlineData("http://localhost:8765", true)]
    [InlineData("http://127.0.0.1:8765", true)]
    [InlineData("https://malicious.example", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void OriginPolicy_UsesExactAllowlist(string? origin, bool expected)
    {
        IReadOnlySet<string> allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "http://localhost:8765",
            "http://127.0.0.1:8765"
        };

        Assert.Equal(expected, OriginPolicy.IsAllowed(origin, allowed));
    }

    [Fact]
    public void ComputeCommandIdentityHash_UsesOnlyNonSensitiveCommandIdentity()
    {
        const string commandId = "00000000-0000-0000-0000-000000000001";
        const string callId = "00000000-0000-0000-0000-000000000002";

        string fingerprint = ProtocolCodec.ComputeCommandIdentityHash("call.start", commandId, callId);
        string sameIdentity = ProtocolCodec.ComputeCommandIdentityHash("call.start", commandId, callId);
        string differentCall = ProtocolCodec.ComputeCommandIdentityHash(
            "call.start",
            commandId,
            "00000000-0000-0000-0000-000000000003");

        Assert.Equal(fingerprint, sameIdentity);
        Assert.NotEqual(fingerprint, differentCall);
        Assert.Equal(64, fingerprint.Length);
    }
}
