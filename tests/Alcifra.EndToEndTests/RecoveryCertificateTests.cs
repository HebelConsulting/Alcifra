using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Api.OfflineArchives;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;
using Alcifra.Infrastructure.Persistence;
using Alcifra.SealedArchive;

namespace Alcifra.EndToEndTests;

// The tenant's recovery certificate (ADR 0960, #1695): a tenant administrator sets, shows and replaces it with
// alcifra, followed from the tenant settings' rel; a non-administrator is refused; a private key in the upload is
// refused; and an offline export made after it is set opens with the recovery key alone. Each test makes its own
// tenant: it changes the setting (ADR 0884).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class RecoveryCertificateTests
{
    private readonly E2EApiFactory _factory;

    public RecoveryCertificateTests(E2EApiFactory factory) => _factory = factory;

    private static (X509Certificate2 WithKey, string Pem, string KeyPem) Rsa(string name)
    {
        using var key = RSA.Create(2048);
        using var made = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var withKey = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        return (withKey, made.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private async Task<(Guid UserId, HttpClient Client)> UserAsync(Guid tenantId, bool admin)
    {
        var email = $"rc-{Guid.NewGuid():N}@e2e.local";
        var id = await _factory.SeedUserAsync(tenantId, email, "rc-123456", admin ? "Recovery Admin" : "Recovery User", isTenantAdmin: admin);
        return (id, _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "rc-123456")));
    }

    private static async Task<string?> HrefAsync(HttpClient client)
    {
        var settings = (await TestJson.Get(client, "/api")).GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "tenantSettings");
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return (await TestJson.Get(client, settings.GetProperty("href").GetString()!)).GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == RecoveryCertificateSurface.Rel).GetProperty("href").GetString();
    }

    [Fact]
    public async Task Saconsole_sets_shows_and_replaces_it_and_set_refuses_to_overwrite()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"rc-{Guid.NewGuid():N}");
        var (_, client) = await UserAsync(tenantId, admin: true);
        var api = new AlcifraApi(client);
        var (first, second) = (Rsa("Recovery one"), Rsa("Recovery two"));

        var (empty, _) = await RecoveryCertificateSurface.ReadAsync(api, CancellationToken.None);
        Assert.False(RecoveryCertificateSurface.Configured(empty));
        await Assert.ThrowsAsync<CliException>(() => RecoveryCertificateSurface.WriteAsync(api, first.Pem, replacing: true, CancellationToken.None));

        var set = await RecoveryCertificateSurface.WriteAsync(api, first.Pem, replacing: false, CancellationToken.None);
        Assert.Equal(first.WithKey.GetCertHashString(HashAlgorithmName.SHA256), set.GetProperty("thumbprint").GetString());
        await Assert.ThrowsAsync<CliException>(() => RecoveryCertificateSurface.WriteAsync(api, second.Pem, replacing: false, CancellationToken.None));

        var replaced = await RecoveryCertificateSurface.WriteAsync(api, second.Pem, replacing: true, CancellationToken.None);
        Assert.Equal("CN=Recovery two", replaced.GetProperty("subject").GetString());
        var (shown, _) = await RecoveryCertificateSurface.ReadAsync(api, CancellationToken.None);
        Assert.Equal(second.Pem, shown.GetProperty("certificatePem").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;   // the tenant filter applies
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        Assert.Equal(2, db.AuditEvents.Count(e => e.Action == "Tenant.RecoveryCertificateSet"));
    }

    [Fact]
    public async Task Only_a_tenant_administrator_reaches_it_and_the_write_needs_the_tag_and_a_public_certificate()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"rc-{Guid.NewGuid():N}");
        var (_, admin) = await UserAsync(tenantId, admin: true);
        var (_, user) = await UserAsync(tenantId, admin: false);
        var href = (await HrefAsync(admin))!;

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(href)).StatusCode);

        using var read = await admin.GetAsync(href);
        var tag = read.Headers.ETag!.Tag;
        var recovery = Rsa("Recovery");

        using (var untagged = await admin.PutAsJsonAsync(href, new { certificatePem = recovery.Pem }))
        {
            Assert.Equal((HttpStatusCode)428, untagged.StatusCode);
        }

        using (var withKey = await SendAsync(admin, href, recovery.Pem + recovery.KeyPem, tag))
        {
            Assert.Equal(HttpStatusCode.BadRequest, withKey.StatusCode);
            Assert.Contains("INVALID_RECOVERY_CERTIFICATE", await withKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var saved = await SendAsync(admin, href, recovery.Pem, tag))
        {
            saved.EnsureSuccessStatusCode();
        }

        using var stale = await SendAsync(admin, href, Rsa("Other").Pem, tag);   // the tag of the first read
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task An_export_after_it_is_set_opens_with_the_recovery_key_alone()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var (adminId, admin) = await UserAsync(tenantId, admin: true);
        var exporterKey = Rsa("Recovery Admin");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            db.Users.Single(u => u.Id == adminId).SmimeCertificatePem = exporterKey.Pem;
            await db.SaveChangesAsync();
        }

        var recovery = Rsa("Escrow");
        await RecoveryCertificateSurface.WriteAsync(new AlcifraApi(admin), recovery.Pem, replacing: false, CancellationToken.None);
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Escrow {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "Folder" });

        using var output = new MemoryStream();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().UserId = adminId;
            await scope.ServiceProvider.GetRequiredService<SealedArchiveExporter>()
                .ExportAsync(new SealedArchiveRequest(repoId, null, false, [], [], "https://e2e.local/api"), output, CancellationToken.None);
        }

        var keys = _factory.Services.GetRequiredService<ArchiveSigningKey>().History;
        using var opened = await SealedArchiveReader.OpenAsync(new MemoryStream(output.ToArray()), keys, (envelope, _) =>
        {
            var cms = new EnvelopedCms();
            cms.Decode(envelope);
            cms.Decrypt(new X509Certificate2Collection(recovery.WithKey));
            return Task.FromResult(cms.ContentInfo.Content);
        });
        Assert.Equal(recovery.WithKey.GetCertHashString(HashAlgorithmName.SHA256),
            opened.Cover.Recipients.Single(r => r.Kind == ArchiveRecipientKind.Recovery).Thumbprint);
        Assert.NotNull(opened.Read("manifest.json"));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string href, string pem, string tag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(new { certificatePem = pem }) };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return client.SendAsync(request);
    }
}
