using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;
using static Alcifra.Api.Provisioning.KristallSamples;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Env-driven idempotent seed of the DEMO tenant (Docker Compose / kiosk only): a tenant administrator with a
/// KNOWN password and a realistic archive, so a visitor can log straight in and see content (ADR 0214).
/// </summary>
/// <remarks>
/// <para>
/// <b>The archive is Kristall Holding AG's</b> (#1788, ADR 1014), filed by <see cref="KristallSamples"/> — the
/// same builder the Manual tenant uses — so the showcase and the manual's screenshots show one company. What is
/// the demo's own is added on top: the two further advertised logins, Anna and Tom, as Kristall employees, and a
/// contact whose card carries a property the structured form does not model. Every sample is fictional; the
/// retired events-and-artists example was the one part of the seed built from real published data (#658), and
/// nothing of it remains.
/// </para>
/// <para>
/// Gated on the demo config being present AND the tenant not already existing, so it is a no-op on restart and
/// in every environment that does not configure it (default appsettings ships the keys empty).
/// </para>
/// </remarks>
public static class DemoDataSeeder
{
    public static async Task SeedIfConfiguredAsync(IServiceProvider services, IConfiguration configuration)
    {
        var demoTenantName = configuration["Demo:Tenant:Name"];
        var demoAdminEmail = configuration["Demo:Administrator:Email"];
        var demoAdminPassword = configuration["Demo:Administrator:Password"];

        if (string.IsNullOrWhiteSpace(demoTenantName)
            || string.IsNullOrWhiteSpace(demoAdminEmail)
            || string.IsNullOrWhiteSpace(demoAdminPassword))
        {
            return;
        }

        var dbContext = services.GetRequiredService<AlcifraDbContext>();

        // Demo timestamps resolve from the keyed "demo-clock" (a fixed instant only under Demo:Clock), NOT the
        // app-wide TimeProvider — so the manual's audit/tasks/my-work screens are byte-stable run-to-run (ADR 0510)
        // while auth keeps the real clock. Production/kiosk leave Demo:Clock unset, so this is System there too.
        var clock = services.GetRequiredKeyedService<TimeProvider>("demo-clock");

        if (await dbContext.Tenants.AnyAsync(t => t.Name == demoTenantName && t.Status == TenantStatus.Active))
        {
            // The one heal that runs on an EXISTING demo volume (#574's lesson): the founding-admin bundle
            // gains rights over time, and a tenant provisioned before one existed strands its admin without
            // it — CanManageMailRouting (#703) arrived after every long-lived dev volume did. Grant-if-absent
            // on the demo admin only; a deliberately revoked right stays revoked... which cannot be told
            // apart from "never had it" for a bool, so on the DEMO tenant the seed's answer wins.
            var normalized = demoAdminEmail.ToUpperInvariant();
            var admin = await dbContext.Users.IgnoreQueryFilters(["TenantFilter"])
                .Where(u => dbContext.Tenants.Any(t => t.Id == u.TenantId && t.Name == demoTenantName))
                .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized);
            if (admin is { CanManageMailRouting: false })
            {
                admin.CanManageMailRouting = true;
                await dbContext.SaveChangesAsync();
            }

            await DemoEmailHeal.HealUnclassifiedEmailsAsync(services, dbContext, demoTenantName, CancellationToken.None);
            return;
        }

        // Deterministic ids (#781): the kiosk's nightly down -v reseeds from scratch, and fresh GUIDs each
        // morning would hand every caching client (IMAP, DAV, WebDAV) a brand-new server wearing yesterday's
        // names. Every id in the demo seed derives from the tenant root + a stable slug instead, so a reseed
        // reproduces the SAME archive. Only fresh volumes are affected — an existing tenant returns above.
        var demoTenantId = DemoId.Root(demoTenantName);
        var provisioned = await services.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(
            demoTenantName,
            demoAdminEmail,
            configuration["Demo:Administrator:DisplayName"] ?? "Demo Admin",
            configuration["Demo:RepositoryName"],
            demoAdminPassword,
            idFor: slug => slug == "tenant" ? demoTenantId : DemoId.For(demoTenantId, slug),
            // createdAt (#832): the Tenant tab renders Created, and a real-clock stamp made every reseed —
            // and every manual capture — show the boot minute there.
            createdAt: clock.GetUtcNow());

        // The archive reads masks/field definitions and relies on SaveChanges' required-field validation, all
        // tenant-filtered — so set the tenant on the accessor the DbContext reads from (nothing did, since this
        // isn't a request). Provisioning above runs before this with no tenant set, like the platform-admin path.
        services.GetRequiredService<CurrentTenantAccessor>().TenantId = provisioned.TenantId;

        var archive = await KristallSamples.SeedAsync(services, provisioned, clock.GetUtcNow(), demoAdminPassword,
            new KristallOptions(
                // None: the folders this archive shares with the demo's earlier one keep their ids (#781).
                FolderSlugPrefix: string.Empty,
                LinkTokenSeed: "simplarchive-demo-seewelle-service-agreement-v1"));
        await SeedAdvertisedColleaguesAsync(archive, demoAdminPassword, provisioned.AdministratorEmail);
        await SeedRawDisclosureContactAsync(archive.Seed);

        // The machine principal a migration reads this tenant WITH (ADR 0585). Seeded from configuration for the
        // same reason as the interop tenant's: a service-account secret is shown once and stored hashed, so a
        // recreated stack otherwise invalidates the tooling's saved credentials. Added after the sample tree so
        // its ACL grant lands on a repository that already has content. No-op unless Demo:ServiceAccount:* is set.
        if (await SeededServiceAccount.AddIfConfiguredAsync(services, dbContext, configuration, "Demo", provisioned))
        {
            await dbContext.SaveChangesAsync();
        }

        // The unattended SETUP principal of the Demo tenant (ADR 0949): what the kiosk's messenger-seed.sh acts as —
        // activating the Messenger, creating its repository, switching on self-service enrolment. Narrow on purpose
        // (ADR 0870), apart from the migration account above: it manages modules and repositories and nothing else,
        // and holds no grant — it is the creator, and so the owner, of the one repository it makes.
        // No-op unless Demo:Setup:ServiceAccount:* is configured, which only the kiosk does.
        if (await SeededServiceAccount.AddIfConfiguredAsync(services, dbContext, configuration, "Demo:Setup", provisioned.TenantId,
                grantOn: null, rights: account => (account.CanManageModules, account.CanManageRepositories) = (true, true), grant: null))
        {
            await dbContext.SaveChangesAsync();
        }

        // The Flight School repository and its unattended setup principal (FlightSchool#85). No-op unless
        // FlightSchoolDemo:ServiceAccount:* is configured, which only the kiosk and the module's local demo do.
        await FlightSchoolDemoSetup.AddIfConfiguredAsync(services, dbContext, configuration, provisioned.TenantId,
            provisioned.AdministratorId, clock.GetUtcNow());

        // The Licensing Module demo's repositories and setup principal (ADR 0901). No-op unless
        // LicensingDemo:ServiceAccount:* is configured, which only that module's local demo does.
        await LicensingDemoSetup.AddIfConfiguredAsync(services, dbContext, configuration, provisioned.TenantId,
            provisioned.AdministratorId, clock.GetUtcNow());
    }

    /// <summary>
    /// The two further logins the README and the kiosk's front page advertise, as Kristall employees (#1788).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Their sign-ins and names are a published promise, so they stay; what changes is the part they play.
    /// <b>Anna Meyer</b> works in Accounting beside Lukas: a member of the group, so she edits the whole
    /// repository and sees the group's shared intray (ADR 0532). <b>Tom Fischer</b> is the assistant, placed like
    /// Sofia: outside the group and granted only reading, so signing in as him is how a visitor watches a
    /// permission refuse something.
    /// </para>
    /// <para>
    /// Both take the administrator's domain rather than a hardcoded one (#432), share the known demo password,
    /// get the same password for DAV and IMAP, and have their mailboxes materialized now (#781).
    /// </para>
    /// </remarks>
    private static async Task SeedAdvertisedColleaguesAsync(KristallArchive archive, string password, string adminEmail)
    {
        var seed = archive.Seed;
        var hasher = new PasswordHasher<User>();
        var anna = NewUser(seed, hasher, password, adminEmail, "anna", "Anna Meyer");
        var tom = NewUser(seed, hasher, password, adminEmail, "tom", "Tom Fischer");
        seed.Db.Users.AddRange(anna, tom);
        await seed.Db.SaveChangesAsync();

        await DemoSeedItems.EnableDavAndImapAsync(seed.Db, hasher, password, [anna.Id, tom.Id]);
        AddToGroup(seed, archive.Team.AccountingGroupId, anna.Id);
        GrantRead(seed, tom.Id, "tom");
        await seed.Db.SaveChangesAsync();

        await EnsureMailboxesAsync(seed, ("anna", anna.Id), ("tom", tom.Id));
    }

    /// <summary>
    /// A card in the administrator's own address book that carries a property the structured form does not model
    /// (<c>X-ABShowAs</c>), so the contact editor's raw disclosure has something to show (#648).
    /// </summary>
    /// <remarks>
    /// Kristall's own cards are plain, and a card this product composes has nothing unmodelled in it, so the
    /// showcase needs one written the way another address book writes it. Added MASKLESS: an address book admits
    /// only Contacts, and the finalizer is what classifies a <c>.vcf</c> and extracts its index fields.
    /// </remarks>
    private static async Task SeedRawDisclosureContactAsync(KristallSeed seed)
    {
        // Idempotent: KristallCustomers already provisioned the space, with the same id family.
        var personal = await seed.Services.GetRequiredService<PersonalRepositoryProvisioner>()
            .EnsureAsync(seed.AdminId, seed.TenantId, CancellationToken.None,
                slug => DemoId.For(seed.TenantId, $"personal/admin/{slug}"), createdAt: seed.Now);
        var addressbookId = await seed.Db.Documents
            .Where(d => d.ParentId == personal.Id && d.Name == PersonalFolders.MyAddressbook)
            .Select(d => (Guid?)d.Id)
            .SingleOrDefaultAsync();
        if (addressbookId is not { } bookId)
        {
            return;
        }

        await seed.DocAsync(bookId, "Seewelle Telecom Hotline", "ManualPersonalHotline.vcf",
            DateOnly.FromDateTime(seed.Now.UtcDateTime), masked: false);
    }
}
