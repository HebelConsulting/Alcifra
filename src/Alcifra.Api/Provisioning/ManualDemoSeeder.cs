using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Acl;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Groups;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Env-driven seed of the MANUAL tenant (#1787): the archive of a fictional Kristall Holding AG that the user
/// manual's automated screenshots are taken against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tenant of its own.</b> The demo tenant is a showcase visitors change all day; a screenshot run needs
/// an archive that looks the same every time it is captured, reset nightly with the rest of the kiosk. It is
/// also where the demo's samples go next (#1788): the events-and-artists example is to retire, so the Customers
/// tree here shows everything that one did — address book, colour-coded calendars, a recurring appointment,
/// the department mailbox — with content written for this company.
/// </para>
/// <para>
/// <b>Every sample has a plain-text source</b> (<c>DemoData/manual/</c>, built by its <c>build.sh</c>):
/// Typst for the PDFs, HTML and CSV for the office documents, SVG for the image, and the e-mail and contact
/// cards are text already. A change to a sample is a reviewable diff, never an opaque binary.
/// </para>
/// <para>
/// Same shape as <see cref="CryptoDemoSeeder"/>: a no-op unless <c>ManualDemo:*</c> is configured, a no-op
/// when the tenant exists, deterministic ids (<see cref="DemoId"/>, #781). Documents carry fixed 2026 dates;
/// appointments and the task's due date are relative to the seeding instant, so after the nightly reset the
/// current month's calendar is never empty in a screenshot.
/// </para>
/// </remarks>
public static class ManualDemoSeeder
{
    public static async Task SeedIfConfiguredAsync(IServiceProvider services, IConfiguration configuration)
    {
        var tenantName = configuration["ManualDemo:Tenant:Name"];
        var adminEmail = configuration["ManualDemo:Administrator:Email"];
        var password = configuration["ManualDemo:Password"];

        if (string.IsNullOrWhiteSpace(tenantName)
            || string.IsNullOrWhiteSpace(adminEmail)
            || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var dbContext = services.GetRequiredService<AlcifraDbContext>();

        // IgnoreQueryFilters: an earlier seeder may have left its own tenant ambient, which would make this
        // existence check a lie.
        if (await dbContext.Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.Name == tenantName && t.Status == TenantStatus.Active))
        {
            return;
        }

        // The demo's clock, so a capture run that pins Demo:Clock gets the same archive every time.
        var clock = services.GetRequiredKeyedService<TimeProvider>("demo-clock");
        var now = clock.GetUtcNow();

        var tenantId = DemoId.Root(tenantName);
        var provisioned = await services.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(
            tenantName,
            adminEmail,
            configuration["ManualDemo:Administrator:DisplayName"] ?? "Petra Keller",
            configuration["ManualDemo:RepositoryName"] ?? "Kristall Holding AG",
            password,
            idFor: slug => slug == "tenant" ? tenantId : DemoId.For(tenantId, slug),
            createdAt: now);

        services.GetRequiredService<CurrentTenantAccessor>().TenantId = provisioned.TenantId;

        var seed = new ManualSeed(
            services, dbContext, services.GetRequiredService<IObjectStorageClient>(),
            services.GetRequiredService<DocumentFinalizer>(), typeof(ManualDemoSeeder).Assembly,
            tenantId, provisioned.AdministratorId, provisioned.RepositoryId, now,
            await dbContext.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.BasicEntry && v.IsCurrent),
            await FolderMask.CurrentVersionIdAsync(dbContext, CancellationToken.None));

        await ConfigureTenantAsync(seed);
        var team = await SeedTeamAsync(seed, password, adminEmail);
        var tree = await SeedTreeAsync(seed);
        await ManualDemoActivity.SeedAsync(seed, team, tree);
        await ManualDemoCustomers.SeedAsync(seed, MailDomain(adminEmail));
    }

    /// <summary>What every step of this seed needs, gathered once instead of threaded through ten parameters.</summary>
    internal sealed record ManualSeed(
        IServiceProvider Services, AlcifraDbContext Db, IObjectStorageClient Storage, DocumentFinalizer Finalizer,
        Assembly Assembly, Guid TenantId, Guid AdminId, Guid RepositoryId, DateTimeOffset Now,
        MaskVersion BasicEntry, Guid? FolderMask)
    {
        public Task<Document> FolderAsync(Guid parentId, string name, string slug, Guid? mask = null) =>
            DemoDataSeeder.AddFolderAsync(Db, TenantId, parentId, name, AdminId, Now, mask ?? FolderMask, $"manual/{slug}");

        public Task<Document> DocAsync(
            Guid parentId, string name, string resource, DateOnly date, DateTimeOffset? at = null,
            string? comment = null, Guid? createdBy = null, bool masked = true) =>
            DemoDataSeeder.AddDocumentAsync(Db, Storage, Assembly, TenantId, parentId, name, createdBy ?? AdminId,
                at ?? Now, masked ? BasicEntry.Id : null, resource, Path.GetExtension(resource), ContentTypeOf(resource),
                date, Finalizer, comment);

        public async Task<DocumentVersion> NextVersionAsync(
            Document document, string resource, DateOnly date, DateTimeOffset at, string comment)
        {
            var version = await DemoDataSeeder.AddVersionAsync(Db, Storage, Assembly, document, AdminId, at,
                document.StorageFolderId, resource, Path.GetExtension(resource), ContentTypeOf(resource), date,
                Finalizer, comment);
            document.CurrentVersionId = version.Id;
            await Db.SaveChangesAsync();
            return version;
        }

        public Task<DocumentVersion> CurrentVersionAsync(Document document) =>
            Db.DocumentVersions.Where(v => v.DocumentId == document.Id)
                .OrderByDescending(v => v.VersionNumber).FirstAsync();
    }

    /// <summary>The people the screenshots show: the administrator and two colleagues.</summary>
    internal sealed record ManualTeam(Guid LukasId, Guid SofiaId, Guid AccountingGroupId);

    /// <summary>The places later steps file into or point at.</summary>
    internal sealed record ManualTree(
        Document Alpenglas, Document SeewelleAgreement, Document March, Document April, Document General);

    internal static string ContentTypeOf(string resource) => Path.GetExtension(resource) switch
    {
        ".pdf" => "application/pdf",
        ".eml" => "message/rfc822",
        ".vcf" => "text/vcard",
        ".png" => "image/png",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        var other => throw new ArgumentException($"No content type for a seeded {other}.", nameof(resource)),
    };

    internal static string MailDomain(string adminEmail) =>
        adminEmail.Split('@') is [_, var domain] && !string.IsNullOrWhiteSpace(domain) ? domain : "alcifra.local";

    // The same showcase switches as the demo tenant, for the same reasons: the external link must answer and
    // show its URL, the quota bar must have a ceiling, and the review task needs an SLA to be overdue against.
    private static async Task ConfigureTenantAsync(ManualSeed seed)
    {
        var tenant = await seed.Db.Tenants.SingleAsync(t => t.Id == seed.TenantId);
        tenant.StorageQuotaBytes = 250L * 1024 * 1024;
        tenant.AllowExternalLinks = true;
        tenant.ShowExternalLinkUrl = true;
        seed.BasicEntry.ReviewSlaDays = 7;
        seed.BasicEntry.RetentionYears = 10;
        await seed.Db.SaveChangesAsync();
    }

    private static async Task<ManualTeam> SeedTeamAsync(ManualSeed seed, string password, string adminEmail)
    {
        var hasher = new PasswordHasher<User>();
        var domain = MailDomain(adminEmail);

        User MakeUser(string localPart, string displayName)
        {
            var user = new User
            {
                Id = DemoId.For(seed.TenantId, $"user/{localPart}"),
                TenantId = seed.TenantId,
                Email = $"{localPart}@{domain}",
                DisplayName = displayName,
                IsActive = true,
                CreatedAt = seed.Now,
                ImapShowAllDocuments = true,
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var lukas = MakeUser("lukas", "Lukas Brunner");
        var sofia = MakeUser("sofia", "Sofia Meier");
        seed.Db.Users.AddRange(lukas, sofia);

        var admin = await seed.Db.Users.SingleAsync(u => u.Id == seed.AdminId);
        admin.ImapShowAllDocuments = true;

        var accounting = new Group { Id = DemoId.For(seed.TenantId, "group/accounting"), TenantId = seed.TenantId, Name = "Accounting" };
        seed.Db.Groups.Add(accounting);
        await seed.Db.SaveChangesAsync();

        await DemoDataSeeder.EnableDavAndImapAsync(seed.Db, hasher, password, [seed.AdminId, lukas.Id, sofia.Id]);

        foreach (var userId in new[] { seed.AdminId, lukas.Id })
        {
            seed.Db.GroupMemberships.Add(new GroupMembership { TenantId = seed.TenantId, UserId = userId, GroupId = accounting.Id });
        }

        // The group (the administrator and Lukas) works in the whole repository; Sofia, outside it, only
        // reads, so Manage access has two kinds of entry to show rather than one.
        seed.Db.AclEntries.Add(new AclEntry
        {
            Id = DemoId.For(seed.TenantId, "acl/repository/accounting"),
            TenantId = seed.TenantId,
            DocumentId = seed.RepositoryId,
            GroupId = accounting.Id,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanCreateSubItems = true,
            CreatedAt = seed.Now,
        });
        seed.Db.AclEntries.Add(new AclEntry
        {
            Id = DemoId.For(seed.TenantId, "acl/repository/sofia"),
            TenantId = seed.TenantId,
            DocumentId = seed.RepositoryId,
            UserId = sofia.Id,
            CanSee = true,
            CanReadContent = true,
            CreatedAt = seed.Now,
        });
        await seed.Db.SaveChangesAsync();

        // A delivery note waiting in the group's intray, so filing from the intray has something to file.
        var note = await DemoDataSeeder.ReadResourceAsync(seed.Assembly, "ManualDeliveryNote.pdf");
        using (var content = new MemoryStream(note))
        {
            await seed.Storage.PutObjectAsync(
                $"{ObjectKeyPrefixes.GroupInbox(seed.TenantId, accounting.Id)}scan-2026-04-delivery-note.pdf",
                content, "application/pdf");
        }

        var mailboxes = seed.Services.GetRequiredService<PersonalMailboxProvisioner>();
        foreach (var (slug, userId) in new[] { ("admin", seed.AdminId), ("lukas", lukas.Id), ("sofia", sofia.Id) })
        {
            await mailboxes.EnsureMailboxAsync(seed.TenantId, userId, CancellationToken.None,
                s => DemoId.For(seed.TenantId, $"personal/{slug}/{s}"), createdAt: seed.Now);
        }

        return new ManualTeam(lukas.Id, sofia.Id, accounting.Id);
    }

    // Business Years / Contracts / General: the filing structure the demo tenant established (#354), with
    // Kristall's own papers in it.
    private static async Task<ManualTree> SeedTreeAsync(ManualSeed seed)
    {
        var businessYears = await seed.FolderAsync(seed.RepositoryId, "Business Years", "business-years");
        var year = await seed.FolderAsync(businessYears.Id, "2026", "business-years/2026");
        string[] monthNames = ["01 January", "02 February", "03 March", "04 April", "05 May", "06 June", "07 July",
            "08 August", "09 September", "10 October", "11 November", "12 December"];
        var months = new Dictionary<int, Document>();
        for (var i = 0; i < monthNames.Length; i++)
        {
            months[i + 1] = await seed.FolderAsync(year.Id, monthNames[i], $"business-years/2026/{i + 1:00}");
        }

        // An e-mail, so the mail rendering and the eMail mask's fields have a sample. Named after its subject,
        // which is what the finalizer's classification names it anyway.
        await seed.DocAsync(months[2].Id, "Ihr Billett Luzern - Zuerich HB / Your ticket Lucerne - Zurich",
            "ManualTrainTicket.eml", new DateOnly(2026, 2, 9));

        // Two versions with annotations on the current one: the corrected head count replaced the first invoice.
        var catering = await seed.DocAsync(months[3].Id, "Catering invoice — staff event", "ManualCateringInvoiceV1.pdf",
            new DateOnly(2026, 3, 20), at: seed.Now.AddHours(-5));
        var cateringV2 = await seed.NextVersionAsync(catering, "ManualCateringInvoiceV2.pdf", new DateOnly(2026, 3, 20),
            seed.Now.AddHours(-4), "Corrected: 42 guests instead of 48.");
        DemoDataSeeder.AddAnnotation(seed.Db, seed.TenantId, catering.Id, cateringV2.Id, seed.AdminId, seed.Now,
            AnnotationKind.Highlight, 0.085, 0.395, 0.830, 0.030, string.Empty, "#ffd54a");
        DemoDataSeeder.AddAnnotation(seed.Db, seed.TenantId, catering.Id, cateringV2.Id, seed.AdminId, seed.Now,
            AnnotationKind.Note, 0.560, 0.560, 0.330, 0.085, "Head count confirmed with HR ✓", "#fff59d");
        await seed.Db.SaveChangesAsync();

        var contracts = await seed.FolderAsync(seed.RepositoryId, "Contracts", "contracts");
        var alpenglas = await seed.FolderAsync(contracts.Id, "Alpenglas AG", "contracts/alpenglas");

        var seewelle = await seed.FolderAsync(contracts.Id, "Seewelle Telecom", "contracts/seewelle");
        var agreement = await seed.DocAsync(seewelle.Id, "Seewelle Telecom — service agreement", "ManualSeewelleContract.pdf",
            new DateOnly(2026, 1, 1));
        var invoices = await seed.FolderAsync(seewelle.Id, "Invoices", "contracts/seewelle/invoices");
        string[] invoiceMonths = ["January", "February", "March"];
        for (var m = 1; m <= invoiceMonths.Length; m++)
        {
            var invoice = await seed.DocAsync(invoices.Id, $"Seewelle invoice — {invoiceMonths[m - 1]} 2026",
                $"ManualSeewelleInvoice{m}.pdf", new DateOnly(2026, m, 1));
            await DemoDataSeeder.AddReferenceAsync(seed.Db, seed.TenantId, months[m].Id, invoice.Id, seed.AdminId, seed.Now);
        }

        var bergkraft = await seed.FolderAsync(contracts.Id, "Bergkraft Energie", "contracts/bergkraft");
        await seed.DocAsync(bergkraft.Id, "Energy cost forecast 2026", "ManualBergkraftForecast.xlsx", new DateOnly(2026, 1, 15));

        var lease = await seed.FolderAsync(contracts.Id, "Office lease Seestrasse 4", "contracts/lease");
        await seed.DocAsync(lease.Id, "Letter to the landlord — ventilation", "ManualLeaseLetter.docx", new DateOnly(2026, 3, 12));

        var general = await seed.FolderAsync(seed.RepositoryId, "General", "general");
        foreach (var name in new[] { "Accounting", "Authorities", "Banks", "Employees", "Insurances" })
        {
            await seed.FolderAsync(general.Id, name, $"general/{name.ToLowerInvariant()}");
        }

        await seed.DocAsync(general.Id, "Site plan Seestrasse 4", "ManualSitePlan.png", new DateOnly(2026, 1, 5));
        var templates = await seed.FolderAsync(general.Id, "Templates", "general/templates");
        await seed.DocAsync(templates.Id, "Letterhead template", "ManualLetterhead.odt", new DateOnly(2026, 1, 1));
        await seed.DocAsync(templates.Id, "Invoice template", "ManualInvoiceTemplate.ods", new DateOnly(2026, 1, 1));

        return new ManualTree(alpenglas, agreement, months[3], months[4], general);
    }
}
