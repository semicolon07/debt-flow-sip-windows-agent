using System.Security.Cryptography;
using System.Text;

namespace DebtFlow.SipAgent.Host;

public sealed record CommandFingerprint(string Value, bool KeyWasCreated);

public interface ICommandFingerprintService
{
    CommandFingerprint Compute(ReadOnlySpan<byte> canonicalPayload);
}

public sealed class DpapiCommandFingerprintService(string keyPath) : ICommandFingerprintService
{
    private static readonly byte[] OptionalEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("DebtFlow.SipAgent.CommandFingerprint.v1"));
    private readonly object _gate = new();
    private byte[]? _key;
    private bool _keyWasCreated;
    private int _creationReported;

    public CommandFingerprint Compute(ReadOnlySpan<byte> canonicalPayload)
    {
        byte[] key = GetOrCreateKey();
        byte[] hash = HMACSHA256.HashData(key, canonicalPayload);
        bool reportCreation = _keyWasCreated && Interlocked.Exchange(ref _creationReported, 1) == 0;
        return new CommandFingerprint($"v2:{Convert.ToHexString(hash).ToLowerInvariant()}", reportCreation);
    }

    private byte[] GetOrCreateKey()
    {
        lock (_gate)
        {
            if (_key != null) return _key;

            string? directory = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(keyPath))
            {
                _key = Unprotect(File.ReadAllBytes(keyPath));
                return _key;
            }

            byte[] generated = RandomNumberGenerator.GetBytes(32);
            byte[] protectedKey = ProtectedData.Protect(
                generated,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            string temporaryPath = $"{keyPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, protectedKey);
                try
                {
                    File.Move(temporaryPath, keyPath);
                    _key = generated;
                    _keyWasCreated = true;
                    return _key;
                }
                catch (IOException) when (File.Exists(keyPath))
                {
                    _key = Unprotect(File.ReadAllBytes(keyPath));
                    return _key;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static byte[] Unprotect(byte[] protectedKey)
    {
        try
        {
            byte[] key = ProtectedData.Unprotect(
                protectedKey,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            if (key.Length != 32) throw new CryptographicException("Invalid command fingerprint key length.");
            return key;
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            throw new AgentConfigurationException("command_key_unavailable");
        }
    }
}
