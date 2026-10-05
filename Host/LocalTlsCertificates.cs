using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DebtFlow.SipAgent.Host;

public interface ILocalTlsCertificateManager
{
    Task<LocalTlsCertificateResult> EnsureReadyAsync(Func<bool> requestFirstRunConsent, CancellationToken cancellationToken);
    Task<LocalTlsCertificateResult> RepairAsync(CancellationToken cancellationToken);
    Task<LocalTlsCertificateResult> InspectAsync(CancellationToken cancellationToken);
    Task<LocalTlsCertificateResult> RemoveAsync(CancellationToken cancellationToken);
}

public sealed record LocalTlsCertificateResult(
    bool IsReady,
    X509Certificate2? Certificate,
    string State,
    int ProfileVersion,
    int? DaysRemaining,
    string? ErrorCode,
    bool IsDegraded = false)
{
    public static LocalTlsCertificateResult Failure(string state, string errorCode, int? daysRemaining = null) =>
        new(false, null, state, LocalTlsCertificateProfile.Version, daysRemaining, errorCode);
}

public sealed record LocalTlsCertificateMetadata(
    int ProfileVersion,
    string Thumbprint,
    string KeyContainerIdentity,
    DateTimeOffset CreatedUtc,
    DateTimeOffset NotAfterUtc);

public interface ILocalTlsClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemLocalTlsClock : ILocalTlsClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ILocalTlsCertificateMetadataStore
{
    LocalTlsCertificateMetadata? Load();
    void Save(LocalTlsCertificateMetadata metadata);
    void Delete();
}

public interface ILocalTlsConsentStore
{
    LocalTlsConsentDecision Decision { get; }
    void Record(LocalTlsConsentDecision decision);
}

public enum LocalTlsConsentDecision
{
    Unknown,
    Accepted,
    Declined
}

public sealed class FileLocalTlsConsentStore(string path) : ILocalTlsConsentStore
{
    public LocalTlsConsentDecision Decision
    {
        get
        {
            if (!File.Exists(path)) return LocalTlsConsentDecision.Unknown;
            try
            {
                return File.ReadAllText(path).Trim() switch
                {
                    "accepted-v1" => LocalTlsConsentDecision.Accepted,
                    "declined-v1" => LocalTlsConsentDecision.Declined,
                    _ => LocalTlsConsentDecision.Unknown
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return LocalTlsConsentDecision.Unknown;
            }
        }
    }

    public void Record(LocalTlsConsentDecision decision)
    {
        if (decision == LocalTlsConsentDecision.Unknown) throw new ArgumentOutOfRangeException(nameof(decision));
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("tls_consent_path_invalid");
        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                decision == LocalTlsConsentDecision.Accepted ? "accepted-v1\n" : "declined-v1\n");
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

public sealed class FileLocalTlsCertificateMetadataStore(string path) : ILocalTlsCertificateMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public LocalTlsCertificateMetadata? Load()
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<LocalTlsCertificateMetadata>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(LocalTlsCertificateMetadata metadata)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("tls_metadata_path_invalid");

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

public interface ILocalTlsCertificatePlatform
{
    X509Certificate2? FindPersonal(string thumbprint);
    X509Certificate2? FindTrustedRoot(string thumbprint);
    CreatedLocalTlsCertificate Create(string keyContainerIdentity, DateTimeOffset now);
    void AddPersonal(X509Certificate2 certificate);
    void AddTrustedRoot(X509Certificate2 certificate);
    void RemovePersonal(string thumbprint);
    void RemoveTrustedRoot(string thumbprint);
    bool MatchesKeyContainer(X509Certificate2 certificate, string keyContainerIdentity);
    void DeleteKey(string keyContainerIdentity);
}

public sealed record CreatedLocalTlsCertificate(X509Certificate2 Certificate, string KeyContainerIdentity);

public sealed class WindowsLocalTlsCertificatePlatform : ILocalTlsCertificatePlatform
{
    public X509Certificate2? FindPersonal(string thumbprint) => Find(StoreName.My, thumbprint);
    public X509Certificate2? FindTrustedRoot(string thumbprint) => Find(StoreName.Root, thumbprint);

    public CreatedLocalTlsCertificate Create(string keyContainerIdentity, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var creation = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None,
            KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption
        };
        creation.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(LocalTlsCertificateProfile.RsaKeySize), CngPropertyOptions.None));

        using CngKey key = CngKey.Create(CngAlgorithm.Rsa, keyContainerIdentity, creation);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest(
            new X500DistinguishedName(LocalTlsCertificateProfile.Subject),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var eku = new OidCollection { new(LocalTlsCertificateProfile.ServerAuthenticationOid) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build(true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        X509Certificate2 certificate = request.CreateSelfSigned(
            now.Subtract(LocalTlsCertificateProfile.NotBeforeBackdate),
            now.Add(LocalTlsCertificateProfile.Validity));
        certificate.FriendlyName = LocalTlsCertificateProfile.FriendlyName;
        return new CreatedLocalTlsCertificate(certificate, keyContainerIdentity);
    }

    public void AddPersonal(X509Certificate2 certificate) => Add(StoreName.My, certificate);

    public void AddTrustedRoot(X509Certificate2 certificate)
    {
        using X509Certificate2 publicCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        publicCertificate.FriendlyName = LocalTlsCertificateProfile.FriendlyName;
        Add(StoreName.Root, publicCertificate);
    }

    public void RemovePersonal(string thumbprint) => Remove(StoreName.My, thumbprint);
    public void RemoveTrustedRoot(string thumbprint) => Remove(StoreName.Root, thumbprint);

    public bool MatchesKeyContainer(X509Certificate2 certificate, string keyContainerIdentity)
    {
        try
        {
            using RSA? rsa = certificate.GetRSAPrivateKey();
            return rsa is RSACng cng &&
                string.Equals(cng.Key.KeyName, keyContainerIdentity, StringComparison.Ordinal) &&
                cng.Key.Provider == CngProvider.MicrosoftSoftwareKeyStorageProvider;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void DeleteKey(string keyContainerIdentity)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CngKey.Exists(keyContainerIdentity, CngProvider.MicrosoftSoftwareKeyStorageProvider)) return;

        using CngKey key = CngKey.Open(keyContainerIdentity, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        key.Delete();
    }

    private static X509Certificate2? Find(StoreName storeName, string thumbprint)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2Collection certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);
        return certificates.Count == 0 ? null : new X509Certificate2(certificates[0]);
    }

    private static void Add(StoreName storeName, X509Certificate2 certificate)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    private static void Remove(StoreName storeName, string thumbprint)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        X509Certificate2Collection certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);
        foreach (X509Certificate2 certificate in certificates) store.Remove(certificate);
    }
}

public static class LocalTlsCertificateProfile
{
    public const int Version = 1;
    public const int RsaKeySize = 3072;
    public const string Subject = "CN=localhost";
    public const string FriendlyName = "Debt Flow SIP Agent Local TLS";
    public const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    public static readonly TimeSpan Validity = TimeSpan.FromDays(365);
    public static readonly TimeSpan NotBeforeBackdate = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RotationThreshold = TimeSpan.FromDays(30);

    public static string? Validate(
        X509Certificate2 certificate,
        DateTimeOffset now,
        bool requirePrivateKey,
        bool requireCurrentlyValid = true)
    {
        if (!string.Equals(certificate.Subject, Subject, StringComparison.OrdinalIgnoreCase)) return "tls_certificate_invalid";
        using RSA? publicKey = certificate.GetRSAPublicKey();
        if (publicKey?.KeySize != RsaKeySize) return "tls_certificate_invalid";
        if (requirePrivateKey && !certificate.HasPrivateKey) return "tls_private_key_unavailable";
        if (!requirePrivateKey && certificate.HasPrivateKey) return "tls_certificate_invalid";
        if (!string.Equals(certificate.SignatureAlgorithm.Value, "1.2.840.113549.1.1.11", StringComparison.Ordinal))
        {
            return "tls_certificate_invalid";
        }
        if (requireCurrentlyValid &&
            (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime()))
        {
            return "tls_certificate_invalid";
        }

        X509BasicConstraintsExtension? constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (constraints == null || constraints.CertificateAuthority) return "tls_certificate_invalid";

        X509KeyUsageExtension? keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        X509KeyUsageFlags expectedUsage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment;
        if (keyUsage == null || keyUsage.KeyUsages != expectedUsage) return "tls_certificate_invalid";

        X509EnhancedKeyUsageExtension? enhancedUsage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (enhancedUsage == null || enhancedUsage.EnhancedKeyUsages.Count != 1 ||
            !string.Equals(enhancedUsage.EnhancedKeyUsages[0].Value, ServerAuthenticationOid, StringComparison.Ordinal))
        {
            return "tls_certificate_invalid";
        }

        X509SubjectAlternativeNameExtension? san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        if (san == null) return "tls_certificate_invalid";
        var dnsNames = san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addresses = san.EnumerateIPAddresses().ToHashSet();
        if (dnsNames.Count != 1 || addresses.Count != 2 || !dnsNames.Contains("localhost") ||
            !addresses.Contains(IPAddress.Loopback) || !addresses.Contains(IPAddress.IPv6Loopback))
        {
            return "tls_certificate_invalid";
        }

        return null;
    }
}

public sealed class LocalTlsCertificateManager(
    ILocalTlsCertificatePlatform platform,
    ILocalTlsCertificateMetadataStore metadataStore,
    ILocalTlsClock clock,
    ILocalTlsConsentStore? consentStore = null) : ILocalTlsCertificateManager
{
    public Task<LocalTlsCertificateResult> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Inspect());
    }

    public Task<LocalTlsCertificateResult> EnsureReadyAsync(
        Func<bool> requestFirstRunConsent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalTlsCertificateMetadata? metadata = metadataStore.Load();
        if (metadata == null)
        {
            LocalTlsConsentDecision decision = consentStore?.Decision ?? LocalTlsConsentDecision.Unknown;
            if (decision == LocalTlsConsentDecision.Declined)
            {
                return Task.FromResult(LocalTlsCertificateResult.Failure("consent_declined", "tls_certificate_consent_declined"));
            }

            if (decision == LocalTlsConsentDecision.Unknown)
            {
                try
                {
                    decision = requestFirstRunConsent()
                        ? LocalTlsConsentDecision.Accepted
                        : LocalTlsConsentDecision.Declined;
                    consentStore?.Record(decision);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return Task.FromResult(LocalTlsCertificateResult.Failure("consent_record_failed", "tls_trust_install_failed"));
                }
            }

            if (decision == LocalTlsConsentDecision.Declined)
            {
                return Task.FromResult(LocalTlsCertificateResult.Failure("consent_declined", "tls_certificate_consent_declined"));
            }

            return Task.FromResult(Provision(null, "tls_trust_install_failed"));
        }

        LocalTlsCertificateResult current = Inspect(metadata);
        if (!current.IsReady) return Task.FromResult(current);
        if (current.Certificate!.NotAfter.ToUniversalTime() - clock.UtcNow > LocalTlsCertificateProfile.RotationThreshold)
        {
            return Task.FromResult(current);
        }

        current.Certificate.Dispose();

        LocalTlsCertificateResult rotated = Provision(metadata, "tls_certificate_rotation_failed");
        if (rotated.IsReady) return Task.FromResult(rotated);

        LocalTlsCertificateResult fallback = Inspect(metadata);
        return Task.FromResult(fallback.IsReady
            ? fallback with { IsDegraded = true, ErrorCode = "tls_certificate_rotation_failed", State = "rotation_degraded" }
            : rotated);
    }

    public Task<LocalTlsCertificateResult> RepairAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            consentStore?.Record(LocalTlsConsentDecision.Accepted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(LocalTlsCertificateResult.Failure("consent_record_failed", "tls_trust_install_failed"));
        }
        return Task.FromResult(Provision(metadataStore.Load(), "tls_trust_install_failed"));
    }

    public Task<LocalTlsCertificateResult> RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalTlsCertificateMetadata? metadata = metadataStore.Load();
        if (metadata == null)
        {
            return Task.FromResult(new LocalTlsCertificateResult(false, null, "removed", LocalTlsCertificateProfile.Version, null, null));
        }

        using X509Certificate2? personal = platform.FindPersonal(metadata.Thumbprint);
        using X509Certificate2? root = platform.FindTrustedRoot(metadata.Thumbprint);
        if (metadata.ProfileVersion != LocalTlsCertificateProfile.Version ||
            personal == null ||
            LocalTlsCertificateProfile.Validate(personal, clock.UtcNow, true, requireCurrentlyValid: false) != null ||
            (root != null && LocalTlsCertificateProfile.Validate(root, clock.UtcNow, false, requireCurrentlyValid: false) != null) ||
            !platform.MatchesKeyContainer(personal, metadata.KeyContainerIdentity))
        {
            return Task.FromResult(LocalTlsCertificateResult.Failure("ownership_mismatch", "tls_certificate_invalid"));
        }

        try
        {
            if (root != null) platform.RemoveTrustedRoot(metadata.Thumbprint);
            platform.RemovePersonal(metadata.Thumbprint);
            platform.DeleteKey(metadata.KeyContainerIdentity);
            metadataStore.Delete();
            return Task.FromResult(new LocalTlsCertificateResult(false, null, "removed", LocalTlsCertificateProfile.Version, null, null));
        }
        catch (Exception exception) when (IsTlsOperationException(exception))
        {
            return Task.FromResult(LocalTlsCertificateResult.Failure("remove_failed", "tls_certificate_remove_failed"));
        }
    }

    private LocalTlsCertificateResult Inspect() => metadataStore.Load() is { } metadata
        ? Inspect(metadata)
        : LocalTlsCertificateResult.Failure("missing", "tls_certificate_invalid");

    private LocalTlsCertificateResult Inspect(LocalTlsCertificateMetadata metadata)
    {
        if (metadata.ProfileVersion != LocalTlsCertificateProfile.Version)
        {
            return LocalTlsCertificateResult.Failure("invalid", "tls_certificate_invalid");
        }

        using X509Certificate2? root = platform.FindTrustedRoot(metadata.Thumbprint);
        X509Certificate2? personal = platform.FindPersonal(metadata.Thumbprint);
        if (personal == null || root == null)
        {
            personal?.Dispose();
            return LocalTlsCertificateResult.Failure("invalid", "tls_certificate_invalid");
        }

        int daysRemaining = Math.Max(0, (int)Math.Floor((personal.NotAfter.ToUniversalTime() - clock.UtcNow).TotalDays));
        if (metadata.NotAfterUtc.UtcDateTime != personal.NotAfter.ToUniversalTime())
        {
            personal.Dispose();
            return LocalTlsCertificateResult.Failure("invalid", "tls_certificate_invalid", daysRemaining);
        }
        string? error = LocalTlsCertificateProfile.Validate(personal, clock.UtcNow, true) ??
            LocalTlsCertificateProfile.Validate(root, clock.UtcNow, false);
        if (error == null && !platform.MatchesKeyContainer(personal, metadata.KeyContainerIdentity))
        {
            error = "tls_private_key_unavailable";
        }

        if (error != null)
        {
            personal.Dispose();
            return LocalTlsCertificateResult.Failure("invalid", error, daysRemaining);
        }

        return new LocalTlsCertificateResult(true, personal, "ready", LocalTlsCertificateProfile.Version, daysRemaining, null);
    }

    private LocalTlsCertificateResult Provision(LocalTlsCertificateMetadata? previous, string failureCode)
    {
        string keyIdentity = $"DebtFlow.SipAgent.LocalTls.v{LocalTlsCertificateProfile.Version}.{Guid.NewGuid():N}";
        CreatedLocalTlsCertificate? created = null;
        string? thumbprint = null;
        bool metadataSwitched = false;
        try
        {
            DateTimeOffset now = clock.UtcNow;
            created = platform.Create(keyIdentity, now);
            thumbprint = created.Certificate.Thumbprint;
            platform.AddPersonal(created.Certificate);
            platform.AddTrustedRoot(created.Certificate);

            using X509Certificate2? installedPersonal = platform.FindPersonal(thumbprint);
            using X509Certificate2? installedRoot = platform.FindTrustedRoot(thumbprint);
            if (installedPersonal == null || installedRoot == null ||
                LocalTlsCertificateProfile.Validate(installedPersonal, now, true) != null ||
                LocalTlsCertificateProfile.Validate(installedRoot, now, false) != null ||
                !platform.MatchesKeyContainer(installedPersonal, keyIdentity))
            {
                throw new CryptographicException("certificate_validation_failed");
            }

            metadataStore.Save(new LocalTlsCertificateMetadata(
                LocalTlsCertificateProfile.Version,
                thumbprint,
                keyIdentity,
                now,
                installedPersonal.NotAfter.ToUniversalTime()));
            metadataSwitched = true;

            LocalTlsCertificateResult activated = Inspect();
            if (!activated.IsReady)
            {
                activated.Certificate?.Dispose();
                throw new CryptographicException("certificate_activation_failed");
            }

            if (previous != null) CleanupPrevious(previous);
            return activated;
        }
        catch (Exception exception) when (IsTlsOperationException(exception))
        {
            if (metadataSwitched)
            {
                TryCleanup(() =>
                {
                    if (previous == null) metadataStore.Delete();
                    else metadataStore.Save(previous);
                });
            }
            if (thumbprint != null)
            {
                TryCleanup(() => platform.RemoveTrustedRoot(thumbprint));
                TryCleanup(() => platform.RemovePersonal(thumbprint));
            }
            TryCleanup(() => platform.DeleteKey(keyIdentity));
            return LocalTlsCertificateResult.Failure("provision_failed", failureCode);
        }
        finally
        {
            created?.Certificate.Dispose();
        }
    }

    private void CleanupPrevious(LocalTlsCertificateMetadata previous)
    {
        using X509Certificate2? personal = platform.FindPersonal(previous.Thumbprint);
        using X509Certificate2? root = platform.FindTrustedRoot(previous.Thumbprint);
        if (personal == null ||
            LocalTlsCertificateProfile.Validate(personal, clock.UtcNow, true, requireCurrentlyValid: false) != null ||
            (root != null && LocalTlsCertificateProfile.Validate(root, clock.UtcNow, false, requireCurrentlyValid: false) != null) ||
            !platform.MatchesKeyContainer(personal, previous.KeyContainerIdentity))
        {
            return;
        }

        if (root != null) TryCleanup(() => platform.RemoveTrustedRoot(previous.Thumbprint));
        TryCleanup(() => platform.RemovePersonal(previous.Thumbprint));
        TryCleanup(() => platform.DeleteKey(previous.KeyContainerIdentity));
    }

    private static bool IsTlsOperationException(Exception exception) =>
        exception is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException;

    private static void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception exception) when (IsTlsOperationException(exception)) { }
    }
}
