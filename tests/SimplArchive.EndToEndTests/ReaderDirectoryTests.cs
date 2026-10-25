using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// Who an audience can be sealed to (ADR 0976): the tenant reader directory an ordinary author reads, and the core's
// decision on a certificate pasted with its attestation chain. Both exist because a sealed document's audience is
// chosen ON THE DEVICE — the server never sees it (ADR 0975) — so the device must be able to learn, from the core
// itself, which certificates may be sealed to.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ReaderDirectoryTests
{
    private readonly E2EApiFactory _factory;

    public ReaderDirectoryTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_directory_names_the_tenants_readers_and_nobody_else()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"Dir{Guid.NewGuid():N}"[..20]);

        var readerId = await _factory.SeedUserAsync(tenantId, Email(), "rd-1234", "Reader Person");
        var pem = NewCertificatePem("Reader Person");
        await _factory.EnrolReaderCertificateAsync(tenantId, readerId, pem);

        // A colleague with no certificate: named nowhere, because nothing can be sealed to them.
        await _factory.SeedUserAsync(tenantId, Email(), "rd-1234", "No Key Person");

        // The caller is an ORDINARY user — the whole point of the directory (an administrator already had a listing).
        var authorEmail = Email();
        await _factory.SeedUserAsync(tenantId, authorEmail, "rd-1234", "Author Person");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(authorEmail, "rd-1234"));

        var directory = await TestJson.Get(api, "/api/reader-directory");
        var reader = Assert.Single(directory.GetProperty("readers").EnumerateArray());

        Assert.Equal(readerId, reader.GetProperty("userId").GetGuid());
        Assert.Equal("Reader Person", reader.GetProperty("displayName").GetString());

        var certificate = Assert.Single(reader.GetProperty("certificates").EnumerateArray());
        Assert.Equal(pem.Trim(), certificate.GetProperty("certificatePem").GetString()!.Trim());
        Assert.Equal("CN=Reader Person", certificate.GetProperty("subject").GetString());
        Assert.Equal("Reader Person", certificate.GetProperty("label").GetString());
        Assert.False(string.IsNullOrEmpty(certificate.GetProperty("thumbprint").GetString()));

        // A search narrows by display name — and one that matches no reader answers nobody.
        Assert.Single((await TestJson.Get(api, "/api/reader-directory?search=Reader"))
            .GetProperty("readers").EnumerateArray());
        Assert.Empty((await TestJson.Get(api, "/api/reader-directory?search=No%20Key"))
            .GetProperty("readers").EnumerateArray());
    }

    [Fact]
    public async Task A_pasted_certificate_is_admitted_only_under_the_tenants_attestation_rule()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"Dir{Guid.NewGuid():N}"[..20]);
        var authorEmail = Email();
        await _factory.SeedUserAsync(tenantId, authorEmail, "rd-1234", "Author Person");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(authorEmail, "rd-1234"));

        var pem = NewCertificatePem("external@example.test");

        // A leaf with NO attestation chain on a tenant that refuses unattested hardware: refused by name. The tenant's
        // rule is enrolment's own (ADR 0963), not a second, weaker one for audiences.
        using (var refused = await ValidateAsync(api, pem, attestation: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("CERTIFICATE_NOT_ATTESTED", await refused.Content.ReadAsStringAsync());
        }

        // The SAME certificate is admitted where the tenant allows hardware without attestation — the other half of
        // that rule, so the refusal above cannot be passing because the endpoint refuses everything.
        await SetUnattestedAllowedAsync(tenantId, allowed: true);
        using (var admitted = await ValidateAsync(api, pem, attestation: null))
        {
            admitted.EnsureSuccessStatusCode();
            var certificate = (await admitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("certificate");
            Assert.Equal("CN=external@example.test", certificate.GetProperty("subject").GetString());
            Assert.Equal(pem.Trim(), certificate.GetProperty("certificatePem").GetString()!.Trim());
        }

        // Bytes that are not a certificate are refused before any attestation question is asked.
        using (var garbage = await ValidateAsync(api, "not a certificate", attestation: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
            Assert.Contains("INVALID_RECIPIENT_CERTIFICATE", await garbage.Content.ReadAsStringAsync());
        }
    }

    private static string Email() => $"reader-{Guid.NewGuid():N}@e2e.local";

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient api, string certificatePem, object? attestation) =>
        api.PostAsJsonAsync("/api/reader-directory/validation", new { certificatePem, attestation });

    private async Task SetUnattestedAllowedAsync(Guid tenantId, bool allowed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.CertificateUnattestedHardwareAllowed = allowed;
        await db.SaveChangesAsync();
    }

    private static string NewCertificatePem(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }
}
