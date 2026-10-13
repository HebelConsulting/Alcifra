using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The caller's certificate set (#1741), reached by the `readerCertificates` rel on /me: the same set envelopes are
// addressed to — the module's where one answers (the Strict tenant), else the user's own column — and why it is empty.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ReaderCertificatesTests
{
    private readonly E2EApiFactory _factory;

    public ReaderCertificatesTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Guid UserId)> UserAsync(Guid tenantId)
    {
        var email = $"certs-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "rc-1234", "Cert User");
        return (_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "rc-1234")), userId);
    }

    private static string Pem(string subject)
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    // Followed from /me, never composed (ADR 0543).
    private static async Task<JsonElement> ReadAsync(HttpClient api)
    {
        var me = await TestJson.Get(api, "/api/me");
        var href = me.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "readerCertificates").GetProperty("href").GetString()!;
        using var head = await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, href));
        Assert.Equal(HttpStatusCode.NoContent, head.StatusCode);
        return await TestJson.Get(api, href);
    }

    [Fact]
    public async Task Without_a_module_the_users_own_certificate_answers_and_none_says_so()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"certs-{Guid.NewGuid():N}");
        var (api, userId) = await UserAsync(tenantId);

        var none = await ReadAsync(api);
        Assert.Equal(("none", "noneEnrolled"), (none.GetProperty("source").GetString(), none.GetProperty("reason").GetString()));

        var pem = Pem("Own Column");
        await _factory.EnrolReaderCertificateAsync(tenantId, userId, pem);   // the user's column on this tenant
        var own = await ReadAsync(api);
        Assert.Equal("own", own.GetProperty("source").GetString());
        var entry = Assert.Single(own.GetProperty("certificates").EnumerateArray().ToList());
        Assert.Equal("Own Column", entry.GetProperty("label").GetString());
        using var certificate = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), entry.GetProperty("thumbprintSha256").GetString());
    }

    [Fact]
    public async Task On_a_strict_tenant_the_modules_set_answers_and_a_withdrawn_one_is_none_enrolled()
    {
        var tenantId = await _factory.SeedTenantNamedAsync(E2EApiFactory.StrictTenantName);
        var (api, userId) = await UserAsync(tenantId);

        await _factory.EnrolReaderCertificateAsync(tenantId, userId, Pem("Card One"));
        var answered = await ReadAsync(api);
        Assert.Equal("module", answered.GetProperty("source").GetString());
        Assert.Single(answered.GetProperty("certificates").EnumerateArray());

        await _factory.WithdrawReaderCertificatesAsync(tenantId, userId);
        var withdrawn = await ReadAsync(api);
        Assert.Equal(("module", "noneEnrolled"), (withdrawn.GetProperty("source").GetString(), withdrawn.GetProperty("reason").GetString()));
    }
}
