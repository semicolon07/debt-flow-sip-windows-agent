using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class LocalTlsCertificateManagerTests
{
    [Fact]
    public async Task First_run_decline_does_not_mutate_certificate_state()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());

        LocalTlsCertificateResult result = await manager.EnsureReadyAsync(() => false, CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("tls_certificate_consent_declined", result.ErrorCode);
        Assert.Equal(0, platform.CreateCount);
        Assert.Null(metadata.Value);
    }

    [Fact]
    public async Task Declined_consent_is_not_requested_again_until_explicit_repair()
    {
        using var platform = new FakePlatform();
        var consent = new FakeConsentStore();
        var manager = new LocalTlsCertificateManager(platform, new FakeMetadataStore(), new FakeClock(), consent);
        int prompts = 0;

        await manager.EnsureReadyAsync(() => { prompts++; return false; }, CancellationToken.None);
        LocalTlsCertificateResult declined = await manager.EnsureReadyAsync(
            () => { prompts++; return true; },
            CancellationToken.None);
        using X509Certificate2? repaired = (await manager.RepairAsync(CancellationToken.None)).Certificate;

        Assert.Equal(1, prompts);
        Assert.Equal("tls_certificate_consent_declined", declined.ErrorCode);
        Assert.NotNull(repaired);
        Assert.Equal(LocalTlsConsentDecision.Accepted, consent.Decision);
    }

    [Fact]
    public async Task First_run_provisions_and_reuses_the_tracked_certificate()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());

        using X509Certificate2? first = (await manager.EnsureReadyAsync(() => true, CancellationToken.None)).Certificate;
        using X509Certificate2? reused = (await manager.EnsureReadyAsync(
            () => throw new InvalidOperationException("consent_must_not_repeat"),
            CancellationToken.None)).Certificate;

        Assert.NotNull(first);
        Assert.NotNull(reused);
        Assert.Equal(first.Thumbprint, reused.Thumbprint);
        Assert.Equal(1, platform.CreateCount);
        Assert.NotNull(metadata.Value);
    }

    [Fact]
    public async Task Accepted_consent_is_not_requested_again_after_a_provisioning_failure()
    {
        using var platform = new FakePlatform { FailCreate = true };
        var consent = new FakeConsentStore();
        var manager = new LocalTlsCertificateManager(
            platform,
            new FakeMetadataStore(),
            new FakeClock(),
            consent);
        int prompts = 0;

        await manager.EnsureReadyAsync(() => { prompts++; return true; }, CancellationToken.None);
        await manager.EnsureReadyAsync(() => { prompts++; return true; }, CancellationToken.None);

        Assert.Equal(1, prompts);
        Assert.Equal(LocalTlsConsentDecision.Accepted, consent.Decision);
    }

    [Fact]
    public async Task Missing_root_copy_fails_closed()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());
        using X509Certificate2? provisioned = (await manager.RepairAsync(CancellationToken.None)).Certificate;
        platform.RemoveTrustedRoot(metadata.Value!.Thumbprint);

        LocalTlsCertificateResult result = await manager.InspectAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("tls_certificate_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Rotation_failure_uses_the_still_valid_old_certificate_with_warning()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var clock = new FakeClock();
        var manager = new LocalTlsCertificateManager(platform, metadata, clock);
        using X509Certificate2? provisioned = (await manager.RepairAsync(CancellationToken.None)).Certificate;
        clock.UtcNow = clock.UtcNow.AddDays(340);
        platform.FailCreate = true;

        LocalTlsCertificateResult result = await manager.EnsureReadyAsync(
            () => throw new InvalidOperationException("consent_must_not_repeat"),
            CancellationToken.None);
        using X509Certificate2? fallback = result.Certificate;
        LocalTlsCertificateResult inspected = await manager.InspectAsync(CancellationToken.None);

        Assert.NotNull(fallback);
        Assert.Equal(provisioned!.Thumbprint, fallback.Thumbprint);
        Assert.True(result.IsDegraded);
        Assert.Equal("tls_certificate_rotation_failed", result.ErrorCode);
        Assert.True(inspected.IsReady);
    }

    [Fact]
    public async Task Partial_trust_install_is_cleaned_up_without_metadata_commit()
    {
        using var platform = new FakePlatform { FailTrustedRootAdd = true };
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());

        LocalTlsCertificateResult result = await manager.RepairAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("tls_trust_install_failed", result.ErrorCode);
        Assert.Null(metadata.Value);
        Assert.False(platform.HasPersonalCertificate);
        Assert.False(platform.HasTrustedRootCertificate);
        Assert.Equal(1, platform.DeleteKeyCount);
    }

    [Fact]
    public async Task Remove_deletes_both_stores_key_and_metadata_for_owned_certificate()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());
        using X509Certificate2? provisioned = (await manager.RepairAsync(CancellationToken.None)).Certificate;

        LocalTlsCertificateResult result = await manager.RemoveAsync(CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Null(metadata.Value);
        Assert.False(platform.HasPersonalCertificate);
        Assert.False(platform.HasTrustedRootCertificate);
        Assert.Equal(1, platform.DeleteKeyCount);
    }

    [Fact]
    public async Task Remove_can_finish_cleanup_when_the_tracked_root_copy_is_already_missing()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());
        using X509Certificate2? provisioned = (await manager.RepairAsync(CancellationToken.None)).Certificate;
        platform.RemoveTrustedRoot(metadata.Value!.Thumbprint);

        LocalTlsCertificateResult result = await manager.RemoveAsync(CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Null(metadata.Value);
        Assert.False(platform.HasPersonalCertificate);
        Assert.Equal(1, platform.DeleteKeyCount);
    }

    [Fact]
    public async Task Remove_refuses_to_delete_when_key_identity_does_not_match()
    {
        using var platform = new FakePlatform();
        var metadata = new FakeMetadataStore();
        var manager = new LocalTlsCertificateManager(platform, metadata, new FakeClock());
        using X509Certificate2? provisioned = (await manager.RepairAsync(CancellationToken.None)).Certificate;
        platform.KeyMatches = false;

        LocalTlsCertificateResult result = await manager.RemoveAsync(CancellationToken.None);

        Assert.Equal("tls_certificate_invalid", result.ErrorCode);
        Assert.NotNull(platform.FindPersonal(metadata.Value!.Thumbprint));
        Assert.Equal(0, platform.DeleteKeyCount);
    }

    [Fact]
    public void Profile_requires_exact_san_eku_and_key_usage()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 valid = CertificateFactory.Create(now, includeIpv6: true, includeServerEku: true);
        using X509Certificate2 missingIpv6 = CertificateFactory.Create(now, includeIpv6: false, includeServerEku: true);
        using X509Certificate2 missingEku = CertificateFactory.Create(now, includeIpv6: true, includeServerEku: false);

        Assert.Null(LocalTlsCertificateProfile.Validate(valid, now, true));
        Assert.Equal("tls_certificate_invalid", LocalTlsCertificateProfile.Validate(missingIpv6, now, true));
        Assert.Equal("tls_certificate_invalid", LocalTlsCertificateProfile.Validate(missingEku, now, true));
    }

    private sealed class FakeClock : ILocalTlsClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeMetadataStore : ILocalTlsCertificateMetadataStore
    {
        public LocalTlsCertificateMetadata? Value { get; private set; }
        public LocalTlsCertificateMetadata? Load() => Value;
        public void Save(LocalTlsCertificateMetadata metadata) => Value = metadata;
        public void Delete() => Value = null;
    }

    private sealed class FakeConsentStore : ILocalTlsConsentStore
    {
        public LocalTlsConsentDecision Decision { get; private set; }
        public void Record(LocalTlsConsentDecision decision) => Decision = decision;
    }

    private sealed class FakePlatform : ILocalTlsCertificatePlatform, IDisposable
    {
        private X509Certificate2? _personal;
        private X509Certificate2? _root;
        private string? _keyIdentity;
        public bool FailCreate { get; set; }
        public bool FailTrustedRootAdd { get; set; }
        public bool KeyMatches { get; set; } = true;
        public int CreateCount { get; private set; }
        public int DeleteKeyCount { get; private set; }
        public bool HasPersonalCertificate => _personal != null;
        public bool HasTrustedRootCertificate => _root != null;

        public X509Certificate2? FindPersonal(string thumbprint) =>
            Matches(_personal, thumbprint) ? CloneWithPrivateKey(_personal!) : null;

        public X509Certificate2? FindTrustedRoot(string thumbprint) =>
            Matches(_root, thumbprint) ? X509CertificateLoader.LoadCertificate(_root!.RawData) : null;

        public CreatedLocalTlsCertificate Create(string keyContainerIdentity, DateTimeOffset now)
        {
            CreateCount++;
            if (FailCreate) throw new CryptographicException("test_create_failure");
            _keyIdentity = keyContainerIdentity;
            return new CreatedLocalTlsCertificate(CertificateFactory.Create(now, true, true), keyContainerIdentity);
        }

        public void AddPersonal(X509Certificate2 certificate)
        {
            _personal?.Dispose();
            _personal = CloneWithPrivateKey(certificate);
        }

        public void AddTrustedRoot(X509Certificate2 certificate)
        {
            if (FailTrustedRootAdd) throw new CryptographicException("test_trust_failure");
            _root?.Dispose();
            _root = X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        public void RemovePersonal(string thumbprint)
        {
            if (!Matches(_personal, thumbprint)) return;
            _personal!.Dispose();
            _personal = null;
        }

        public void RemoveTrustedRoot(string thumbprint)
        {
            if (!Matches(_root, thumbprint)) return;
            _root!.Dispose();
            _root = null;
        }

        public bool MatchesKeyContainer(X509Certificate2 certificate, string keyContainerIdentity) =>
            KeyMatches && string.Equals(_keyIdentity, keyContainerIdentity, StringComparison.Ordinal);

        public void DeleteKey(string keyContainerIdentity)
        {
            if (string.Equals(_keyIdentity, keyContainerIdentity, StringComparison.Ordinal))
            {
                DeleteKeyCount++;
                _keyIdentity = null;
            }
        }

        public void Dispose()
        {
            _personal?.Dispose();
            _root?.Dispose();
        }

        private static bool Matches(X509Certificate2? certificate, string thumbprint) =>
            certificate != null && string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);

        private static X509Certificate2 CloneWithPrivateKey(X509Certificate2 certificate) =>
            X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    private static class CertificateFactory
    {
        public static X509Certificate2 Create(DateTimeOffset now, bool includeIpv6, bool includeServerEku)
        {
            using RSA rsa = RSA.Create(LocalTlsCertificateProfile.RsaKeySize);
            var request = new CertificateRequest(
                LocalTlsCertificateProfile.Subject,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            var eku = new OidCollection();
            if (includeServerEku) eku.Add(new Oid(LocalTlsCertificateProfile.ServerAuthenticationOid));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            if (includeIpv6) san.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(san.Build(true));
            return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(365));
        }
    }
}
