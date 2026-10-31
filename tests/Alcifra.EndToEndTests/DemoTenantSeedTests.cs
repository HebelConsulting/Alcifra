using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Api.Provisioning;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Workflow;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

/// <summary>
/// The Demo tenant's seed files Kristall Holding AG's archive through the same builder as the Manual tenant
/// (#1788, ADR 1014), keeps the advertised logins in their new roles, adds its own extras, and a second start
/// leaves it alone.
/// </summary>
/// <remarks>
/// <para>
/// The UI suites read this seed all day, but only through the screens they test; what they cannot see is the
/// shape of the tenant as a whole — who may do what, and that nothing of the retired samples is left. A seed
/// that throws takes the application down at boot, and on the kiosk this one runs every night.
/// </para>
/// <para>
/// It runs in the same database as <see cref="ManualTenantSeedTests"/>, so the two tenants' fixed external-link
/// tokens are also proven not to collide: the token is unique across the installation, and the kiosk runs both.
/// </para>
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DemoTenantSeedTests(E2EApiFactory factory)
{
    private const string Password = "Demo-Seed-2026!";

    [Fact]
    public async Task The_demo_tenant_files_kristalls_archive_with_the_advertised_logins_and_its_extras()
    {
        var run = Guid.NewGuid().ToString("N")[..10];
        var tenantName = $"Demo-{run}";
        var domain = $"demo-{run}.e2e.local";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Tenant:Name"] = tenantName,
            ["Demo:Administrator:Email"] = $"admin@{domain}",
            ["Demo:Administrator:Password"] = Password,
            ["Demo:RepositoryName"] = "Demo Repository",
        }).Build();

        using (var scope = factory.Services.CreateScope())
        {
            await DemoDataSeeder.SeedIfConfiguredAsync(scope.ServiceProvider, configuration);
        }

        // Idempotent: the second start finds the tenant, runs only the existing-volume heals, and adds nothing
        // (a duplicate deterministic id would throw).
        using (var scope = factory.Services.CreateScope())
        {
            await DemoDataSeeder.SeedIfConfiguredAsync(scope.ServiceProvider, configuration);
        }

        var tenantId = DemoId.Root(tenantName);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();

            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Equal(TenantStatus.Active, tenant.Status);
            Assert.Equal(250L * 1024 * 1024, tenant.StorageQuotaBytes);
            Assert.True(tenant.AllowExternalLinks);
            Assert.True(tenant.ShowExternalLinkUrl);

            // Kristall's tree, in the repository the demo configures.
            var repository = await db.Documents.SingleAsync(d => d.ParentId == null && d.Name == "Demo Repository");
            var top = await db.Documents.Where(d => d.ParentId == repository.Id).Select(d => d.Name).ToListAsync();
            Assert.Superset(new HashSet<string> { "Business Years", "Contracts", "General", "Customers", "Company calendar" }, top.ToHashSet());

            // The folders shared with the earlier archive keep their ids: no folder-slug prefix on this tenant.
            Assert.Equal(DemoId.For(tenantId, "folder/contracts"),
                await db.Documents.Where(d => d.ParentId == repository.Id && d.Name == "Contracts").Select(d => d.Id).SingleAsync());

            // Nothing of the retired samples is left.
            foreach (var retired in new[] { "Acme Corp", "Departments", "Events", "MyCountry Telekom", "Invoice 2026-003", "Offer 2026-014" })
            {
                Assert.False(await db.Documents.AnyAsync(d => d.Name == retired), $"'{retired}' is still seeded");
            }

            Assert.False(await db.DocumentVersions.AnyAsync(v => v.Status != DocumentVersionStatus.Confirmed));

            // The showcase on Kristall's documents: the invoice under review with its annotations, tags, thread
            // and reference; the offer's two versions; the multi-page agreement; the shared link.
            var invoice = await db.Documents.SingleAsync(d => d.Name == "Invoice AG-2026-031");
            Assert.Equal(3, await db.DocumentAnnotations.CountAsync(a => a.DocumentId == invoice.Id));
            Assert.Equal(2, await db.DocumentTags.CountAsync(t => t.DocumentId == invoice.Id));
            Assert.Equal(WorkflowStatus.InReview, await db.WorkflowStates
                .Where(w => db.DocumentVersions.Any(v => v.Id == w.DocumentVersionId && v.DocumentId == invoice.Id))
                .Select(w => w.Status).SingleAsync());
            Assert.True(await db.DocumentReferences.AnyAsync(r => r.TargetDocumentId == invoice.Id));
            Assert.True(await db.ChatMessages.AnyAsync(m => m.DocumentId == invoice.Id && m.ParentMessageId != null));
            var offer = await db.Documents.SingleAsync(d => d.Name == "Offer 2026-022");
            Assert.Equal(2, await db.DocumentVersions.CountAsync(v => v.DocumentId == offer.Id));
            var agreement = await db.Documents.SingleAsync(d => d.Name == "Seewelle Telecom — service agreement");
            Assert.Equal(1, await db.ExternalLinks.CountAsync(l => l.DocumentId == agreement.Id && l.MaxAccesses == null));

            var basicEntry = await db.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.BasicEntry && v.IsCurrent);
            Assert.Equal((7, 10), (basicEntry.ReviewSlaDays, basicEntry.RetentionYears));

            // The advertised logins: Anna works in Accounting with Lukas, Tom only reads, like Sofia.
            var users = await db.Users.ToDictionaryAsync(u => u.Email.Split('@')[0], u => u);
            Assert.Equal(new[] { "admin", "anna", "lukas", "sofia", "tom" }, users.Keys.Order().ToArray());
            Assert.Equal(("Anna Meyer", "Tom Fischer"), (users["anna"].DisplayName, users["tom"].DisplayName));
            Assert.All(new[] { "anna", "tom" }, u => Assert.EndsWith($"@{domain}", users[u].Email));

            var accounting = await db.Groups.SingleAsync(g => g.Name == "Accounting");
            var members = await db.GroupMemberships.Where(m => m.GroupId == accounting.Id).Select(m => m.UserId).ToListAsync();
            Assert.Equal(new[] { users["admin"].Id, users["anna"].Id, users["lukas"].Id }.Order(), members.Order());

            var tomGrant = await db.AclEntries.SingleAsync(a => a.UserId == users["tom"].Id && a.DocumentId == repository.Id);
            Assert.True(tomGrant.CanSee && tomGrant.CanReadContent);
            Assert.False(tomGrant.CanEditContent || tomGrant.CanEditIndexData || tomGrant.CanCreateSubItems);

            // The demo's own card: the raw disclosure's sample, beside Kristall's two personal contacts.
            var contactMask = await db.MaskVersions.Where(v => v.MaskId == WellKnownMaskIds.Contact).Select(v => v.Id).ToListAsync();
            Assert.True(await db.Documents.AnyAsync(d => d.Name == "Seewelle Telecom Hotline" && contactMask.Contains(d.MaskVersionId)));
            Assert.Equal(6, await db.Documents.CountAsync(d => contactMask.Contains(d.MaskVersionId)));
        }

        // Every advertised login signs in with the configured password.
        foreach (var local in new[] { "admin", "anna", "tom" })
        {
            Assert.False(string.IsNullOrEmpty(await factory.GetUserTokenAsync($"{local}@{domain}", Password)));
        }
    }
}
