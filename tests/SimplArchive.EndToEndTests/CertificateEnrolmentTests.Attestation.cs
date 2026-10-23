using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.TestSupport;

namespace SimplArchive.EndToEndTests;

// Attested enrolment end to end (#1790, ADR 0963): the platform administrator's roots (API and saconsole, If-Match), a
// tenant's selection (one PUT under the tenant's tag), the enrolment door's named refusals, the way back for a key enrolled
// without its attestation, and the reader answer following every switch at once. Each test uploads its OWN test root, so
// the installation-wide table is shared without one test's switch reaching another's.
public partial class CertificateEnrolmentTests
{
    private async Task<SimplArchiveApi> PlatformAsync()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        return new SimplArchiveApi(_factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret)));
    }

    private static object Evidence(TestKey key, string? attestationPem = null) =>
        new { format = "piv", attestationCertificatePem = attestationPem ?? key.AttestationPem, intermediateCertificatePem = key.IntermediatePem, slot = "9d" };

    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()!;
    }

    private static async Task<int> ReaderCountAsync(HttpClient member)
    {
        var me = Links(await TestJson.Get(member, "/api/me"));
        return (await TestJson.Get(member, me["readerCertificates"])).GetProperty("certificates").GetArrayLength();
    }

    [Fact]
    public async Task The_platform_administrator_manages_roots_with_saconsole_and_the_catalogue_ships_switched_off()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        var http = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var platform = new SimplArchiveApi(http);
        var listing = await AttestationRootSurface.ListAsync(platform, CancellationToken.None);
        var catalogue = AttestationRootSurface.Roots(listing).Where(r => r.GetProperty("catalogueKey").ValueKind == JsonValueKind.String).ToList();
        Assert.Contains(catalogue, r => r.GetProperty("catalogueKey").GetString() == "piv-attestation-root-2016");
        Assert.Contains(catalogue, r => r.GetProperty("catalogueKey").GetString() == "attestation-root-2024");

        using var hierarchy = TestAttestationHierarchy.Direct($"Admin {Guid.NewGuid():N}");
        var added = await AttestationRootSurface.AddAsync(platform, hierarchy.RootPem, "Test root", enabled: false, CancellationToken.None);
        Assert.False(added.GetProperty("enabled").GetBoolean());
        await Assert.ThrowsAsync<CliException>(() => AttestationRootSurface.AddAsync(platform, hierarchy.RootPem, null, false, CancellationToken.None));   // 409

        var id = added.GetProperty("id").GetString()!;
        var enabled = await AttestationRootSurface.ChangeAsync(platform, id, (name, _) => (name, true), CancellationToken.None);
        Assert.True(enabled.GetProperty("enabled").GetBoolean());
        var renamed = await AttestationRootSurface.ChangeAsync(platform, id, (_, on) => ("Renamed root", on), CancellationToken.None);
        Assert.Equal("Renamed root", renamed.GetProperty("name").GetString());

        // The If-Match chain on one root: none → 428, a stale tag → 412.
        var self = $"/api/attestation-roots/{id}";
        using (var untagged = await http.PutAsJsonAsync(self, new { name = "x", enabled = false }))
        {
            Assert.Equal((HttpStatusCode)428, untagged.StatusCode);
        }

        using var read = await http.GetAsync(self);
        var tag = read.Headers.ETag!.Tag;
        await AttestationRootSurface.ChangeAsync(platform, id, (name, _) => (name, false), CancellationToken.None);
        using (var stale = await PutWithTagAsync(http, self, new { name = "x", enabled = true }, tag))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }
    }

    [Fact]
    public async Task A_tenant_user_is_refused_the_installations_roots()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            Assert.DoesNotContain("attestationRoots", Links(await TestJson.Get(rig.Admin, "/api")).Keys);
            using var refused = await rig.Admin.GetAsync("/api/attestation-roots");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task Enrolment_refuses_by_name_and_an_attested_key_addresses_its_holder_until_its_root_is_switched_off()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId, unattested: false);
            using var hierarchy = TestAttestationHierarchy.WithIntermediates($"Enrol {Guid.NewGuid():N}");
            using var stranger = TestAttestationHierarchy.Direct($"Stranger {Guid.NewGuid():N}");
            var platform = await PlatformAsync();
            var root = await AttestationRootSurface.AddAsync(platform, hierarchy.RootPem, null, enabled: true, CancellationToken.None);
            var key = hierarchy.NewKey("member");

            using (var none = await rig.Member.PostAsJsonAsync("/api/certificates", new { certificatePem = key.CertificatePem, label = "Card" }))
            {
                Assert.Equal("CERTIFICATE_NOT_ATTESTED", await RefusalAsync(none));
            }

            using (var other = await rig.Member.PostAsJsonAsync("/api/certificates",
                       new { certificatePem = key.CertificatePem, label = "Card", attestation = Evidence(key, hierarchy.NewKey("x").AttestationPem) }))
            {
                Assert.Equal("CERTIFICATE_ATTESTATION_KEY_MISMATCH", await RefusalAsync(other));
            }

            var strangerKey = stranger.NewKey("member");
            using (var unknown = await rig.Member.PostAsJsonAsync("/api/certificates",
                       new { certificatePem = strangerKey.CertificatePem, label = "Card", attestation = Evidence(strangerKey) }))
            {
                Assert.Equal("CERTIFICATE_ATTESTATION_ROOT_NOT_TRUSTED", await RefusalAsync(unknown));
            }

            using (var garbage = await rig.Member.PostAsJsonAsync("/api/certificates",
                       new { certificatePem = key.CertificatePem, label = "Card", attestation = Evidence(key, "garbage") }))
            {
                Assert.Equal("CERTIFICATE_ATTESTATION_INVALID", await RefusalAsync(garbage));
            }

            var enrolled = await TestJson.Post(rig.Member, "/api/certificates", new { certificatePem = key.CertificatePem, label = "Card", attestation = Evidence(key) });
            Assert.Equal("attested", enrolled.GetProperty("attestation").GetString());
            Assert.Equal(hierarchy.DeviceSerial, enrolled.GetProperty("deviceSerial").GetString());
            Assert.True(enrolled.GetProperty("usable").GetBoolean());
            Assert.Equal(1, await ReaderCountAsync(rig.Member));

            // The tenant switches the root off with saconsole: one PUT of the whole list under the tenant's tag.
            var admin = new SimplArchiveApi(rig.Admin);
            var rootId = root.GetProperty("id").GetString()!;
            var selection = await TenantAttestationRootSurface.SwitchAsync(admin, rootId, off: true, CancellationToken.None);
            Assert.Contains(Guid.Parse(rootId), TenantAttestationRootSurface.Disabled(selection));
            Assert.Equal(0, await ReaderCountAsync(rig.Member));
            var listed = Assert.Single((await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray().ToList());
            Assert.Equal("rootNotTrusted", listed.GetProperty("attestation").GetString());

            await TenantAttestationRootSurface.SwitchAsync(admin, rootId, off: false, CancellationToken.None);
            Assert.Equal(1, await ReaderCountAsync(rig.Member));

            // ... and the installation switches it off: out for every tenant, at once.
            await AttestationRootSurface.ChangeAsync(platform, rootId, (name, _) => (name, false), CancellationToken.None);
            Assert.Equal(0, await ReaderCountAsync(rig.Member));

            await using var scope = _factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = rig.TenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
            Assert.Equal(4, await db.AuditEvents.CountAsync(e => e.Action == "Certificate.EnrolmentRefused"));
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "Certificate.AttestationRootsSelected"));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "AttestationRoot.Changed"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task The_tenants_root_selection_needs_the_tenants_tag()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var href = Links(await TestJson.Get(rig.Admin, "/api"))["certificateAttestationRoots"];
            using var read = await rig.Admin.GetAsync(href);
            var tag = read.Headers.ETag!.Tag;
            var anyRoot = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roots")[0].GetProperty("id").GetGuid();

            using (var untagged = await rig.Admin.PutAsJsonAsync(href, new { disabledRootIds = new[] { anyRoot } }))
            {
                Assert.Equal((HttpStatusCode)428, untagged.StatusCode);
            }

            using (var unknown = await PutWithTagAsync(rig.Admin, href, new { disabledRootIds = new[] { Guid.NewGuid() } }, tag))
            {
                Assert.Equal("CERTIFICATE_ATTESTATION_ROOT_UNKNOWN", await RefusalAsync(unknown));
            }

            using (var saved = await PutWithTagAsync(rig.Admin, href, new { disabledRootIds = new[] { anyRoot } }, tag))
            {
                saved.EnsureSuccessStatusCode();
            }

            using (var stale = await PutWithTagAsync(rig.Admin, href, new { disabledRootIds = Array.Empty<Guid>() }, tag))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            }

            using var member = await rig.Member.GetAsync(href);
            Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task A_key_enrolled_WITHOUT_its_attestation_drops_out_and_its_holder_brings_it_back_by_enrolling_it_again_with_it()
    {
        // The rollout's way back (owner, 2026-10-22): an existing enrolment without evidence addresses nobody once the
        // tenant requires attestation; its holder enrols the SAME key again with the token's attestation, no new key.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            using var hierarchy = TestAttestationHierarchy.Direct($"Back {Guid.NewGuid():N}");
            await AttestationRootSurface.AddAsync(await PlatformAsync(), hierarchy.RootPem, null, enabled: true, CancellationToken.None);
            var key = hierarchy.NewKey("member");

            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId, unattested: true);
            var plain = await TestJson.Post(rig.Member, "/api/certificates", new { certificatePem = key.CertificatePem, label = "Card" });
            Assert.Equal("notAttested", plain.GetProperty("attestation").GetString());
            Assert.Equal(1, await ReaderCountAsync(rig.Member));

            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId, unattested: false);
            Assert.Equal(0, await ReaderCountAsync(rig.Member));

            using var again = await rig.Member.PostAsJsonAsync("/api/certificates", new { certificatePem = key.CertificatePem, label = "Card", attestation = Evidence(key) });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);   // the existing enrolment, now attested; not a new one
            var attested = await again.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(plain.GetProperty("documentId").GetGuid(), attested.GetProperty("documentId").GetGuid());
            Assert.Equal("attested", attested.GetProperty("attestation").GetString());
            Assert.Equal(1, await ReaderCountAsync(rig.Member));

            // A second time is "already enrolled", as before.
            using var third = await rig.Member.PostAsJsonAsync("/api/certificates", new { certificatePem = key.CertificatePem, label = "Card", attestation = Evidence(key) });
            Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }
}
