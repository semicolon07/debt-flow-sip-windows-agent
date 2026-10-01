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
}
