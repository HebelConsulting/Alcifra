using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Acl;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Groups;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// The archive of the fictional Kristall Holding AG (#1787, ADR 0932), filed into a freshly provisioned tenant:
/// the ONE builder both the Manual tenant and the Demo tenant call (#1788, ADR 1014).
/// </summary>
/// <remarks>
/// <para>
/// <b>One builder, two tenants.</b> The Manual tenant is the stable archive the manual's screenshots are taken
/// against; the Demo tenant is the showcase visitors change all day. Both show the same company, so both file
/// it through this class, and what differs is passed in (<see cref="KristallOptions"/>) rather than copied: a
/// second copy of a seed is a second place for a sample to drift. The Demo tenant adds its extras on top
/// (<see cref="DemoDataSeeder"/>).
/// </para>
/// <para>
/// <b>Every sample has a plain-text source</b> (<c>DemoData/manual/</c>, built by its <c>build.sh</c>):
/// Typst for the PDFs, HTML and CSV for the office documents, SVG for the image, and the e-mail and contact
/// cards are text already. A change to a sample is a reviewable diff, never an opaque binary.
/// </para>
/// <para>
/// Deterministic ids (<see cref="DemoId"/>, #781). Documents carry fixed 2026 dates; appointments and the task's
/// due date are relative to the seeding instant, so after the nightly reset the current month's calendar is
/// never empty.
/// </para>
/// </remarks>
internal static class KristallSamples
{
    /// <summary>What differs between the tenants that file Kristall's archive.</summary>
    /// <param name="FolderSlugPrefix">
    /// Prepended to every folder's id slug. The Manual tenant was born with <c>manual/</c>; the Demo tenant
    /// passes none, so the folders it shares with its earlier archive (Business Years and its months,
    /// Contracts, General and its children) keep the ids a caching client already holds (#781).
    /// </param>
    /// <param name="LinkTokenSeed">
    /// What the seeded external link's token derives from. Tokens are unique across the installation, and the
    /// kiosk runs both tenants, so each passes its own.
    /// </param>
    internal sealed record KristallOptions(string FolderSlugPrefix, string LinkTokenSeed);

    /// <summary>What every step of this seed needs, gathered once instead of threaded through ten parameters.</summary>
    internal sealed record KristallSeed(
        IServiceProvider Services, AlcifraDbContext Db, IObjectStorageClient Storage, DocumentFinalizer Finalizer,
        Assembly Assembly, Guid TenantId, Guid AdminId, Guid RepositoryId, DateTimeOffset Now,
        MaskVersion BasicEntry, Guid? FolderMask, string FolderSlugPrefix)
    {
        public Task<Document> FolderAsync(Guid parentId, string name, string slug, Guid? mask = null) =>
            DemoSeedItems.AddFolderAsync(Db, TenantId, parentId, name, AdminId, Now, mask ?? FolderMask, $"{FolderSlugPrefix}{slug}");

        public Task<Document> DocAsync(
            Guid parentId, string name, string resource, DateOnly date, DateTimeOffset? at = null,
            string? comment = null, Guid? createdBy = null, bool masked = true) =>
            DemoSeedItems.AddDocumentAsync(Db, Storage, Assembly, TenantId, parentId, name, createdBy ?? AdminId,
                at ?? Now, masked ? BasicEntry.Id : null, resource, Path.GetExtension(resource), ContentTypeOf(resource),
                date, Finalizer, comment);

        public async Task<DocumentVersion> NextVersionAsync(
            Document document, string resource, DateOnly date, DateTimeOffset at, string comment)
        {
            var version = await DemoSeedItems.AddVersionAsync(Db, Storage, Assembly, document, AdminId, at,
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

    /// <summary>Kristall's people: the administrator's two colleagues and the group with the shared intray.</summary>
    internal sealed record KristallTeam(Guid LukasId, Guid SofiaId, Guid AccountingGroupId);

    /// <summary>The places later steps file into or point at.</summary>
    internal sealed record KristallTree(
        Document Alpenglas, Document SeewelleAgreement, Document March, Document April, Document General);

    /// <summary>The archive as filed, for a caller that adds to it.</summary>
    internal sealed record KristallArchive(KristallSeed Seed, KristallTeam Team, KristallTree Tree);

    /// <summary>
    /// Files Kristall's whole archive into <paramref name="provisioned"/>: tenant switches, team, filing tree,
    /// the work in progress, customers and calendars.
    /// </summary>
    /// <remarks>
    /// The caller sets the ambient tenant first: the steps read masks and field definitions through the
    /// tenant query filter, and seeding is not a request.
    /// </remarks>
    internal static async Task<KristallArchive> SeedAsync(
        IServiceProvider services, ProvisionedTenant provisioned, DateTimeOffset now, string password, KristallOptions options)
    {
        var dbContext = services.GetRequiredService<AlcifraDbContext>();
        var seed = new KristallSeed(
            services, dbContext, services.GetRequiredService<IObjectStorageClient>(),
            services.GetRequiredService<DocumentFinalizer>(), typeof(KristallSamples).Assembly,
            provisioned.TenantId, provisioned.AdministratorId, provisioned.RepositoryId, now,
            await dbContext.MaskVersions.SingleAsync(v => v.MaskId == WellKnownMaskIds.BasicEntry && v.IsCurrent),
            await FolderMask.CurrentVersionIdAsync(dbContext, CancellationToken.None),
            options.FolderSlugPrefix);

        await ConfigureTenantAsync(seed);
        var team = await SeedTeamAsync(seed, password, provisioned.AdministratorEmail);
        var tree = await SeedTreeAsync(seed);
        await KristallActivity.SeedAsync(seed, team, tree, options.LinkTokenSeed);
        await KristallCustomers.SeedAsync(seed, MailDomain(provisioned.AdministratorEmail));
        return new KristallArchive(seed, team, tree);
    }

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

    /// <summary>
    /// The domain the seeded logins share, taken from the administrator's address (#432): a deployment that
    /// renames the administrator must not hand out credentials straddling two domains.
    /// </summary>
    internal static string MailDomain(string adminEmail) =>
        adminEmail.Split('@') is [_, var domain] && !string.IsNullOrWhiteSpace(domain) ? domain : "alcifra.local";

    // The showcase switches: the external link must answer and show its URL (ADR 0546, #412), the quota bar
    // must have a ceiling, and the review task needs an SLA to be overdue against and a retention period to
    // put the documents on the Retention schedule. A real tenant opts into each of these deliberately.
    private static async Task ConfigureTenantAsync(KristallSeed seed)
    {
        var tenant = await seed.Db.Tenants.SingleAsync(t => t.Id == seed.TenantId);
        tenant.StorageQuotaBytes = 250L * 1024 * 1024;
        tenant.AllowExternalLinks = true;
        tenant.ShowExternalLinkUrl = true;
        seed.BasicEntry.ReviewSlaDays = 7;
        seed.BasicEntry.RetentionYears = 10;
        await seed.Db.SaveChangesAsync();
    }

    private static async Task<KristallTeam> SeedTeamAsync(KristallSeed seed, string password, string adminEmail)
    {
        var hasher = new PasswordHasher<User>();
        var lukas = NewUser(seed, hasher, password, adminEmail, "lukas", "Lukas Brunner");
        var sofia = NewUser(seed, hasher, password, adminEmail, "sofia", "Sofia Meier");
        seed.Db.Users.AddRange(lukas, sofia);

        var admin = await seed.Db.Users.SingleAsync(u => u.Id == seed.AdminId);
        admin.ImapShowAllDocuments = true;

        var accounting = new Group { Id = DemoId.For(seed.TenantId, "group/accounting"), TenantId = seed.TenantId, Name = "Accounting" };
        seed.Db.Groups.Add(accounting);
        await seed.Db.SaveChangesAsync();

        await DemoSeedItems.EnableDavAndImapAsync(seed.Db, hasher, password, [seed.AdminId, lukas.Id, sofia.Id]);
        AddToGroup(seed, accounting.Id, seed.AdminId, lukas.Id);

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
        GrantRead(seed, sofia.Id, "sofia");
        await seed.Db.SaveChangesAsync();

        // A delivery note waiting in the group's intray (ADR 0532), so filing from the intray has something to
        // file: a raw object under the group's intray prefix, the way a scanner's upload arrives.
        var note = await DemoSeedItems.ReadResourceAsync(seed.Assembly, "ManualDeliveryNote.pdf");
        using (var content = new MemoryStream(note))
        {
            await seed.Storage.PutObjectAsync(
                $"{ObjectKeyPrefixes.GroupInbox(seed.TenantId, accounting.Id)}scan-2026-04-delivery-note.pdf",
                content, "application/pdf");
        }

        await EnsureMailboxesAsync(seed, ("admin", seed.AdminId), ("lukas", lukas.Id), ("sofia", sofia.Id));
        return new KristallTeam(lukas.Id, sofia.Id, accounting.Id);
    }

    /// <summary>A colleague at the administrator's domain, signing in with the tenant's known password.</summary>
    internal static User NewUser(
        KristallSeed seed, PasswordHasher<User> hasher, string password, string adminEmail, string localPart, string displayName)
    {
        var user = new User
        {
            Id = DemoId.For(seed.TenantId, $"user/{localPart}"),
            TenantId = seed.TenantId,
            Email = $"{localPart}@{MailDomain(adminEmail)}",
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = seed.Now,
            ImapShowAllDocuments = true,
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        return user;
    }

    internal static void AddToGroup(KristallSeed seed, Guid groupId, params Guid[] userIds)
    {
        foreach (var userId in userIds)
        {
            seed.Db.GroupMemberships.Add(new GroupMembership { TenantId = seed.TenantId, UserId = userId, GroupId = groupId });
        }
    }

    /// <summary>Read access to the whole repository for someone outside the group that works in it.</summary>
    internal static void GrantRead(KristallSeed seed, Guid userId, string slug) =>
        seed.Db.AclEntries.Add(new AclEntry
        {
            Id = DemoId.For(seed.TenantId, $"acl/repository/{slug}"),
            TenantId = seed.TenantId,
            DocumentId = seed.RepositoryId,
            UserId = userId,
            CanSee = true,
            CanReadContent = true,
            CreatedAt = seed.Now,
        });

    /// <summary>
    /// Materializes each login's mailbox NOW, deterministically (#781). Left to first use, the mailbox and its
    /// standing folders would be minted with fresh GUIDs whenever a visitor's client (or ingress mail) first
    /// touched them — exactly the identity churn the deterministic ids exist to remove.
    /// </summary>
    internal static async Task EnsureMailboxesAsync(KristallSeed seed, params (string Slug, Guid UserId)[] users)
    {
        var mailboxes = seed.Services.GetRequiredService<PersonalMailboxProvisioner>();
        foreach (var (slug, userId) in users)
        {
            await mailboxes.EnsureMailboxAsync(seed.TenantId, userId, CancellationToken.None,
                s => DemoId.For(seed.TenantId, $"personal/{slug}/{s}"), createdAt: seed.Now);
        }
    }

    // Business Years / Contracts / General: the filing structure the demo tenant established (#354), with
    // Kristall's own papers in it.
    private static async Task<KristallTree> SeedTreeAsync(KristallSeed seed)
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
        DemoSeedItems.AddAnnotation(seed.Db, seed.TenantId, catering.Id, cateringV2.Id, seed.AdminId, seed.Now,
            AnnotationKind.Highlight, 0.085, 0.395, 0.830, 0.030, string.Empty, "#ffd54a");
        DemoSeedItems.AddAnnotation(seed.Db, seed.TenantId, catering.Id, cateringV2.Id, seed.AdminId, seed.Now,
            AnnotationKind.Note, 0.560, 0.560, 0.330, 0.085, "Head count confirmed with HR ✓", "#fff59d");
        await seed.Db.SaveChangesAsync();

        var contracts = await seed.FolderAsync(seed.RepositoryId, "Contracts", "contracts");
        var alpenglas = await seed.FolderAsync(contracts.Id, "Alpenglas AG", "contracts/alpenglas");

        // The supplier invoices live under their contract AND are referenced into their Business Years month —
        // the multi-filing showcase (ADR "…move and reference").
        var seewelle = await seed.FolderAsync(contracts.Id, "Seewelle Telecom", "contracts/seewelle");
        var agreement = await seed.DocAsync(seewelle.Id, "Seewelle Telecom — service agreement", "ManualSeewelleContract.pdf",
            new DateOnly(2026, 1, 1));
        var invoices = await seed.FolderAsync(seewelle.Id, "Invoices", "contracts/seewelle/invoices");
        string[] invoiceMonths = ["January", "February", "March"];
        for (var m = 1; m <= invoiceMonths.Length; m++)
        {
            var invoice = await seed.DocAsync(invoices.Id, $"Seewelle invoice — {invoiceMonths[m - 1]} 2026",
                $"ManualSeewelleInvoice{m}.pdf", new DateOnly(2026, m, 1));
            await DemoSeedItems.AddReferenceAsync(seed.Db, seed.TenantId, months[m].Id, invoice.Id, seed.AdminId, seed.Now);
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

        return new KristallTree(alpenglas, agreement, months[3], months[4], general);
    }
}
