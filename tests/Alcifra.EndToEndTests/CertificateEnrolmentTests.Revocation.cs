using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

// Revoking a key from the clients (#2278, ADR 1083): its holder, or an administrator who manages users, with a reason,
// through the listed key's own `revoke` rel. One commit writes Revoked on and the reason; the holder is told when somebody
// else did it; a revocation is final.
public partial class CertificateEnrolmentTests
{
    [Fact]
    public async Task A_holder_revokes_their_own_key_with_a_reason_and_it_stays_revoked()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            var root = Links(await TestJson.Get(rig.Member, "/api"));
            await TestJson.Post(rig.Member, root["certificates"], new { certificatePem = SelfSigned("member", rig.MemberEmail), label = "Lost card" });

            var mine = (await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().Single();
            var revoke = Links(mine)["revoke"];
            using (var response = await rig.Member.PostAsJsonAsync(revoke, new { reason = "lost" }))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            var after = (await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().Single();
            Assert.Equal(JsonValueKind.String, after.GetProperty("revokedAt").ValueKind);
            Assert.Equal("lost", after.GetProperty("revocationReason").GetString());
            Assert.False(after.GetProperty("usable").GetBoolean());
            Assert.DoesNotContain("revoke", Links(after).Keys);

            using var again = await rig.Member.PostAsJsonAsync(revoke, new { reason = "lost" });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("CERTIFICATE_ALREADY_REVOKED", await again.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    // #2317: a revoked certificate used to block its own re-enrolment for good, with a refusal saying to revoke it first. The
    // revoked enrolment stays in history and the same certificate is enrolled again beside it, for its holder or somebody
    // else (owner, 2026-11-11); two UNREVOKED enrolments of it are still refused.
    [Fact]
    public async Task A_revoked_certificate_is_enrolled_again_for_its_holder_or_somebody_else()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            // Possession binding, as on the kiosk's Demo tenant: an administrator enrols a key for whoever holds it.
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            await SetSanMustMatchAccountAsync(rig.Admin, false);
            var certificates = Links(await TestJson.Get(rig.Admin, "/api"))["certificates"];
            var forMember = $"{certificates}?holder={Uri.EscapeDataString(rig.MemberEmail)}";
            var pem = SelfSigned("phone key", rig.MemberEmail);
            await TestJson.Post(rig.Admin, certificates, new { certificatePem = pem, label = "Phone key", holder = rig.MemberEmail });
            await RevokeAsync(rig.Member, (await TestJson.Get(rig.Member, certificates)).EnumerateArray().Single());   // by its holder

            // Its holder gets it again: the revoked entry stays, the new one is usable.
            await TestJson.Post(rig.Admin, certificates, new { certificatePem = pem, label = "Phone key, again", holder = rig.MemberEmail });
            var both = (await TestJson.Get(rig.Admin, forMember)).EnumerateArray().ToList();
            Assert.Equal(2, both.Count);
            Assert.Single(both, k => k.GetProperty("usable").GetBoolean() && k.GetProperty("label").GetString() == "Phone key, again");
            Assert.Single(both, k => k.GetProperty("revokedAt").ValueKind == JsonValueKind.String);

            // While that enrolment stands, nobody else may hold the certificate.
            using (var refused = await rig.Admin.PostAsJsonAsync(certificates, new { certificatePem = pem, label = "Taken" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains("CERTIFICATE_ALREADY_ENROLLED", await refused.Content.ReadAsStringAsync());
            }

            // Revoked again, it goes to somebody else, as on a phone whose other profile enrols the same key.
            await RevokeAsync(rig.Member, (await TestJson.Get(rig.Member, certificates)).EnumerateArray().Single(k => k.GetProperty("usable").GetBoolean()));
            await TestJson.Post(rig.Admin, certificates, new { certificatePem = pem, label = "Admin's now" });
            Assert.Equal("Admin's now", (await TestJson.Get(rig.Admin, certificates)).EnumerateArray().Single().GetProperty("label").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    private static async Task RevokeAsync(HttpClient holder, JsonElement key)
    {
        using var response = await holder.PostAsJsonAsync(Links(key)["revoke"], new { reason = "replaced" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Nobody_else_revokes_a_key_but_a_user_administrator_who_tells_the_holder()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            var root = Links(await TestJson.Get(rig.Admin, "/api"));
            await TestJson.Post(rig.Admin, root["certificates"], new { certificatePem = SelfSigned("admin", rig.AdminEmail), label = "Admin card" });
            await TestJson.Post(rig.Member, root["certificates"], new { certificatePem = SelfSigned("member", rig.MemberEmail), label = "Member card" });
            var adminKey = (await TestJson.Get(rig.Admin, $"{root["certificates"]}?holder={Uri.EscapeDataString(rig.AdminEmail)}")).EnumerateArray().Single();

            // A member may not revoke somebody else's key, even by its address.
            using (var refused = await rig.Member.PostAsJsonAsync(Links(adminKey)["revoke"], new { reason = "lost" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains("CERTIFICATE_REVOCATION_NOT_ALLOWED", await refused.Content.ReadAsStringAsync());
            }

            // A tenant administrator without "manage users" sees the member's key but is offered no revoke.
            var memberKey = (await TestJson.Get(rig.Admin, $"{root["certificates"]}?holder={Uri.EscapeDataString(rig.MemberEmail)}")).EnumerateArray().Single();
            Assert.DoesNotContain("revoke", Links(memberKey).Keys);

            // With it, the administrator revokes it, giving words of their own, and the holder is told.
            await GrantManageUsersAsync(rig.AdminEmail);
            memberKey = (await TestJson.Get(rig.Admin, $"{root["certificates"]}?holder={Uri.EscapeDataString(rig.MemberEmail)}")).EnumerateArray().Single();
            using (var bad = await rig.Admin.PostAsJsonAsync(Links(memberKey)["revoke"], new { reason = "other" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                Assert.Contains("CERTIFICATE_REVOCATION_REASON_INVALID", await bad.Content.ReadAsStringAsync());
            }

            using (var revoked = await rig.Admin.PostAsJsonAsync(Links(memberKey)["revoke"], new { reason = "other", text = "Card swapped at the front desk" }))
            {
                Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            }

            var seen = (await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().Single();
            Assert.Equal("Card swapped at the front desk", seen.GetProperty("revocationReason").GetString());
            var told = (await TestJson.Get(rig.Member, "/api/notifications")).GetProperty("notifications").EnumerateArray()
                .Where(n => n.GetProperty("title").GetString()!.Contains("Member card", StringComparison.Ordinal)).ToList();
            Assert.Single(told);
            Assert.Contains("Card swapped at the front desk", told[0].GetProperty("body").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    // A key filed before the Certificate mask gained Revocation reason wears an older mask version. Its revocation moves
    // it to the current version with its values carried over, or the reason would be stored against no field it has.
    [Fact]
    public async Task A_key_filed_under_an_older_mask_version_keeps_its_fields_and_gets_its_reason()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            var root = Links(await TestJson.Get(rig.Member, "/api"));
            await TestJson.Post(rig.Member, root["certificates"], new { certificatePem = SelfSigned("member", rig.MemberEmail), label = "Old card" });
            var key = (await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().Single();
            var documentId = key.GetProperty("documentId").GetGuid();
            await AddCertificateMaskVersionAsync(rig.TenantId);

            using (var response = await rig.Member.PostAsJsonAsync(Links(key)["revoke"], new { reason = "replaced" }))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var document = await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId);
            var current = await db.MaskVersions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(v => v.TenantId == rig.TenantId && v.MaskId == WellKnownMaskIds.Certificate && v.IsCurrent);
            Assert.Equal(current.Id, document.MaskVersionId);
            var fields = await db.FieldValues.IgnoreQueryFilters().AsNoTracking().Where(v => v.DocumentId == documentId)
                .Join(db.FieldDefinitions.IgnoreQueryFilters(), v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, f.MaskVersionId, v.Value })
                .ToListAsync();
            Assert.All(fields, f => Assert.Equal(current.Id, f.MaskVersionId));
            Assert.Equal("Old card", fields.Single(f => f.Name == "Label").Value);
            Assert.Equal("replaced", fields.Single(f => f.Name == "Revocation reason").Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    private async Task GrantManageUsersAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.NormalizedEmail == normalized);
        user.CanManageUsers = true;
        await db.SaveChangesAsync();
    }

    // A newer version of the tenant's Certificate mask, as the seeder makes when the mask gains a field: the same fields
    // and one more. SaveChanges numbers it and makes it current.
    private async Task AddCertificateMaskVersionAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var currentVersion = await db.MaskVersions.IgnoreQueryFilters()
            .SingleAsync(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Certificate && v.IsCurrent);
        var fields = await db.FieldDefinitions.IgnoreQueryFilters().AsNoTracking().Where(f => f.MaskVersionId == currentVersion.Id).ToListAsync();
        var next = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = WellKnownMaskIds.Certificate, Name = currentVersion.Name, CreatedAt = DateTimeOffset.UtcNow };
        db.MaskVersions.Add(next);
        foreach (var field in fields.Append(new FieldDefinition { Name = "Added later", DataType = FieldDataType.Text }))
        {
            db.FieldDefinitions.Add(new FieldDefinition
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskVersionId = next.Id,
                Name = field.Name,
                DataType = field.DataType,
                IsRequired = field.IsRequired,
                IsList = field.IsList,
                SortOrder = field.SortOrder,
            });
        }

        await db.SaveChangesAsync();
    }
}
