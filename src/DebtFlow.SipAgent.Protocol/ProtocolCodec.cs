using System.Text;
using System.Text.Json;

namespace DebtFlow.SipAgent.Protocol;

public sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ProtocolCodec
{
    private static readonly HashSet<string> EnvelopeFields =
        ["protocolVersion", "kind", "messageId", "sentAtUtc", "type", "payload"];

    public static ProtocolEnvelope Deserialize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > ProtocolConstants.MaximumMessageBytes)
        {
            throw new ProtocolException("message_too_large", "Message size is invalid.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException("invalid_message", "Message must be a JSON object.");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!EnvelopeFields.Contains(property.Name))
                {
                    throw new ProtocolException("invalid_message", "Message contains an unknown field.");
                }
            }

            int version = GetRequiredInt32(root, "protocolVersion");
            if (version != ProtocolConstants.Version)
            {
                throw new ProtocolException("protocol_version_unsupported", "Protocol version is unsupported.");
            }

            string kind = GetRequiredString(root, "kind", 32);
            string messageId = GetRequiredUuid(root, "messageId");
            string type = GetRequiredString(root, "type", 80);
            DateTimeOffset sentAtUtc = GetRequiredTimestamp(root, "sentAtUtc");

            if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException("invalid_message", "Payload must be an object.");
            }

            return new ProtocolEnvelope(version, kind, messageId, sentAtUtc, type, payload.Clone());
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("invalid_message", $"Invalid JSON: {ex.GetType().Name}.");
        }
    }

    public static byte[] Serialize<T>(string kind, string type, T payload, string? messageId = null)
    {
        var envelope = new
        {
            protocolVersion = ProtocolConstants.Version,
            kind,
            messageId = messageId ?? NewId(),
            sentAtUtc = DateTimeOffset.UtcNow,
            type,
            payload
        };

        return JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
    }

    public static T DeserializePayload<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(ProtocolJson.Options)
                ?? throw new ProtocolException("invalid_message", "Payload is required.");
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("invalid_message", $"Payload is invalid: {ex.GetType().Name}.");
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    public static void ValidateUuid(string value, string fieldName)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new ProtocolException("invalid_message", $"{fieldName} must be a UUID.");
        }
    }

    public static string ComputeRequestHash(JsonElement payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload.GetRawText());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string GetRequiredUuid(JsonElement root, string name)
    {
        string value = GetRequiredString(root, name, 64);
        ValidateUuid(value, name);
        return value;
    }

    private static string GetRequiredString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) ||
            property.GetString()!.Length > maximumLength)
        {
            throw new ProtocolException("invalid_message", $"{name} is invalid.");
        }

        return property.GetString()!;
    }

    private static int GetRequiredInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property) || !property.TryGetInt32(out int value))
        {
            throw new ProtocolException("invalid_message", $"{name} is invalid.");
        }

        return value;
    }

    private static DateTimeOffset GetRequiredTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(property.GetString(), out DateTimeOffset value))
        {
            throw new ProtocolException("invalid_message", $"{name} is invalid.");
        }

        return value.ToUniversalTime();
    }
}
