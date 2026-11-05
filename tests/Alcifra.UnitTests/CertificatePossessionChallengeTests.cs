using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Alcifra.Api.Certificates;
using Alcifra.Api.Errors.Exceptions.Certificates;
using Alcifra.ApiClient;

namespace Alcifra.UnitTests;

// ADR 1061: what a possession proof must be, each refusal by its own exception. The token is stateless, so only its own
// claims say who, which certificate and until when.
public sealed class CertificatePossessionChallengeTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private readonly MovableClock _clock = new();
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    private readonly X509Certificate2 _exportable;
    private readonly CertificatePossessionChallenge _challenge;

    public CertificatePossessionChallengeTests()
    {
        _certificate = Certificate(_key);
        _exportable = X509CertificateLoader.LoadPkcs12(_certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        _challenge = new CertificatePossessionChallenge(DataProtectionProvider.Create("possession-tests"), _clock);
    }

    public void Dispose()
    {
        _exportable.Dispose();
        _certificate.Dispose();
        _key.Dispose();
    }

    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static X509Certificate2 Certificate(RSA key) =>
        new CertificateRequest("CN=holder", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero));

    // The client's real steps: the envelope, opened by the key, read as every opener reads it.
    private PossessionProof Answered()
    {
        var (envelope, token) = _challenge.Issue(Tenant, User, _certificate);
        var opened = SoftwareKeyEnvelope.TryOpen(SmimeEnvelope.Unwrap(Alcifra.ApiClient.PasswordStore.PasswordStoreClient.AsSmime(envelope)), [_exportable]);
        return new PossessionProof(token, Convert.ToBase64String(opened!.Value.Bytes));
    }

    [Fact]
    public void The_key_opens_the_challenge_and_its_answer_verifies() => _challenge.Verify(Answered(), Tenant, User, _certificate);

    [Fact]
    public void An_answer_still_holds_at_the_end_of_the_five_minutes_and_not_after()
    {
        var proof = Answered();
        _clock.Advance(TimeSpan.FromMinutes(5));
        _challenge.Verify(proof, Tenant, User, _certificate);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<CertificateChallengeExpiredException>(() => _challenge.Verify(proof, Tenant, User, _certificate));
    }

    [Fact]
    public void Another_user_or_tenant_cannot_use_it()
    {
        var proof = Answered();
        Assert.Throws<CertificateChallengeForeignException>(() => _challenge.Verify(proof, Tenant, Guid.NewGuid(), _certificate));
        Assert.Throws<CertificateChallengeForeignException>(() => _challenge.Verify(proof, Guid.NewGuid(), User, _certificate));
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    public void A_damaged_token_is_refused_as_foreign(string token) =>
        Assert.Throws<CertificateChallengeForeignException>(() => _challenge.Verify(Answered() with { Token = token }, Tenant, User, _certificate));

    [Fact]
    public void It_proves_only_the_certificate_it_was_issued_for()
    {
        using var other = RSA.Create(2048);
        using var otherCertificate = Certificate(other);
        Assert.Throws<CertificateChallengeOtherCertificateException>(() => _challenge.Verify(Answered(), Tenant, User, otherCertificate));
    }

    [Theory]
    [InlineData("AAAA")]
    [InlineData("not base64!")]
    public void An_answer_that_is_not_the_nonce_is_refused(string nonce) =>
        Assert.Throws<CertificateChallengeWrongAnswerException>(() => _challenge.Verify(Answered() with { Nonce = nonce }, Tenant, User, _certificate));

    [Fact]
    public void A_token_says_nothing_readable_about_who_it_is_for()
    {
        var (_, token) = _challenge.Issue(Tenant, User, _certificate);
        Assert.DoesNotContain(User.ToString(), token, StringComparison.OrdinalIgnoreCase);
    }
}
