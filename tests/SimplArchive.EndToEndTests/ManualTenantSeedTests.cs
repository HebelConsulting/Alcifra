using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.Provisioning;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// The Manual tenant's seed (#1787) produces the archive the manual's screenshots need, through the real
/// filing path, and a second start leaves it alone.
/// </summary>
/// <remarks>
/// A seed that throws takes the application down at boot, and on the kiosk this one runs every night; so it is
/// run here against real Postgres, object storage and the finalizer rather than trusted. The assertions name
/// what the manual's chapters show, so a sample that stops being seeded fails here, not in a blank screenshot.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ManualTenantSeedTests(E2EApiFactory factory)
{
    private const string Password = "Manual-Demo-2026!";

    [Fact]
    public async Task The_manual_tenant_is_seeded_with_every_sample_the_manual_shows()
    {
        var run = Guid.NewGuid().ToString("N")[..10];
        var tenantName = $"Manual-{run}";
        var adminEmail = $"petra@kristall-{run}.e2e.local";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ManualDemo:Tenant:Name"] = tenantName,
            ["ManualDemo:Administrator:Email"] = adminEmail,
            ["ManualDemo:Password"] = Password,
        }).Build();

        using (var scope = factory.Services.CreateScope())
        {
            await ManualDemoSeeder.SeedIfConfiguredAsync(scope.ServiceProvider, configuration);
        }

        // Idempotent: the second start finds the tenant and does nothing (it would throw on a duplicate id).
        using (var scope = factory.Services.CreateScope())
        {
            await ManualDemoSeeder.SeedIfConfiguredAsync(scope.ServiceProvider, configuration);
        }

        var tenantId = DemoId.Root(tenantName);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Equal(TenantStatus.Active, tenant.Status);

            var repository = await db.Documents.SingleAsync(d => d.ParentId == null && d.Name == "Kristall Holding AG");
            var top = await db.Documents.Where(d => d.ParentId == repository.Id).Select(d => d.Name).ToListAsync();
            Assert.Superset(new HashSet<string> { "Business Years", "Contracts", "General", "Customers", "Company calendar" }, top.ToHashSet());

            // Every version went through the finalizer: none is left pending.
            Assert.False(await db.DocumentVersions.AnyAsync(v => v.Status != DocumentVersionStatus.Confirmed));

            var invoice = await db.Documents.SingleAsync(d => d.Name == "Invoice AG-2026-031");
            Assert.Equal(3, await db.DocumentAnnotations.CountAsync(a => a.DocumentId == invoice.Id));
            Assert.Equal(2, await db.DocumentTags.CountAsync(t => t.DocumentId == invoice.Id));
            Assert.True(await db.ChatMessageMentions.AnyAsync());
            Assert.Equal(WorkflowStatus.InReview, await db.WorkflowStates
                .Where(w => db.DocumentVersions.Any(v => v.Id == w.DocumentVersionId && v.DocumentId == invoice.Id))
                .Select(w => w.Status).SingleAsync());
            Assert.True(await db.DocumentReferences.AnyAsync(r => r.TargetDocumentId == invoice.Id));

            var offer = await db.Documents.SingleAsync(d => d.Name == "Offer 2026-022");
            Assert.Equal(2, await db.DocumentVersions.CountAsync(v => v.DocumentId == offer.Id));

            Assert.Equal(2, await db.Notifications.CountAsync(n => n.ReadAt == null));
            Assert.Equal(1, await db.ExternalLinks.CountAsync());
            Assert.Equal(2, await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).CountAsync(d => d.DeletedAt != null));

            // The finalizer classified the composed and the written items: appointments and contact cards.
            var appointmentMask = await MaskOf(db, WellKnownMaskIds.Appointment);
            var contactMask = await MaskOf(db, WellKnownMaskIds.Contact);
            Assert.Equal(8, await db.Documents.CountAsync(d => appointmentMask.Contains(d.MaskVersionId)));
            Assert.Equal(5, await db.Documents.CountAsync(d => contactMask.Contains(d.MaskVersionId)));

            // The e-mail was classified too: the eMail mask's fields are filled from its header.
            var mailId = DemoId.For(tenantId, "doc/ManualTrainTicket");
            Assert.True(await db.FieldValues.AnyAsync(f => f.DocumentId == mailId));
        }

        // And the administrator signs in with the configured password.
        Assert.False(string.IsNullOrEmpty(await factory.GetUserTokenAsync(adminEmail, Password)));
    }

    private static Task<List<Guid>> MaskOf(SimplArchiveDbContext db, Guid maskId) =>
        db.MaskVersions.Where(v => v.MaskId == maskId).Select(v => v.Id).ToListAsync();
}
