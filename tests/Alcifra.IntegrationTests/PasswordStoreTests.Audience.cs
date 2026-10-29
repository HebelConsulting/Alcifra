using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Acl;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Groups;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Acl;

namespace Alcifra.IntegrationTests;

// Encrypted content's audience (ADR 0983): who a store's entries are sealed to is its DECLARED audience intersected with
// its readers — never "every reader", never the administrator's bypass — and somebody who leaves it loses, at the next
// re-seal, even the superseded versions their key still opens.
public sealed partial class PasswordStoreTests
{
    private async Task<Guid> PersonAsync(string name, bool tenantAdmin = false)
    {
        var id = Guid.NewGuid();
        using var db = Ctx();
        db.Users.Add(new User { Id = id, TenantId = _tenantId, Email = $"{name}@t.test", DisplayName = name, IsTenantAdmin = tenantAdmin, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task GrantReadAsync(Guid documentId, Guid? userId = null, Guid? groupId = null)
    {
        using var db = Ctx();
        db.AclEntries.Add(new AclEntry { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = documentId, UserId = userId, GroupId = groupId, CanSee = true, CanReadContent = true, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task DeclareAsync(Guid documentId, Guid? userId = null, Guid? groupId = null, bool manager = false)
    {
        using var db = Ctx();
        db.ContentAudienceMembers.Add(new ContentAudienceMember { Id = Guid.NewGuid(), TenantId = _tenantId, DocumentId = documentId, UserId = userId, GroupId = groupId, IsManager = manager, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<IEnumerable<Guid>> SealedToAsync(Guid storeId)
    {
        using var db = Ctx();
        return (await Readers(db).ForStoreAsync(storeId, CancellationToken.None)).Select(r => r.UserId).Order();
    }

    [Fact]
    public async Task Entries_are_sealed_to_the_declared_audience_that_reads_never_to_every_reader()
    {
        var (repository, store) = await StoreAsync();
        var bob = await PersonAsync("Bob");
        await GrantReadAsync(repository, _userId);
        await GrantReadAsync(repository, bob);

        // Both read the store; only the declared one is sealed to.
        await DeclareAsync(store, _userId, manager: true);
        Assert.Equal([_userId], await SealedToAsync(store));

        // Declared but no longer a reader is not in effect: losing read takes a member out without touching the rows.
        var carol = await PersonAsync("Carol");
        await DeclareAsync(store, carol);
        Assert.Equal([_userId], await SealedToAsync(store));

        await GrantReadAsync(repository, carol);
        Assert.Equal(new[] { _userId, carol }.Order(), await SealedToAsync(store));
    }

    [Fact]
    public async Task The_administrators_bypass_never_makes_anyone_a_member()
    {
        var (repository, store) = await StoreAsync();
        var admin = await PersonAsync("Admin", tenantAdmin: true);
        await GrantReadAsync(repository, _userId);
        await DeclareAsync(store, _userId, manager: true);
        await DeclareAsync(store, admin);

        using (var db = Ctx())
        {
            // The bypass lets the administrator read; it does not make them somebody entries are sealed to.
            Assert.True((await new EffectiveRightsCalculator(db).GetEffectiveRightsAsync(admin, store)).CanReadContent);
        }

        Assert.Equal([_userId], await SealedToAsync(store));
        await GrantReadAsync(repository, admin);
        Assert.Equal(new[] { _userId, admin }.Order(), await SealedToAsync(store));
    }

    [Fact]
    public async Task A_declared_group_seals_to_its_members_who_read()
    {
        var (repository, store) = await StoreAsync();
        var bob = await PersonAsync("Bob");
        var outsider = await PersonAsync("Outsider");
        var team = Guid.NewGuid();
        using (var db = Ctx())
        {
            db.Groups.Add(new Group { Id = team, TenantId = _tenantId, Name = "Team" });
            db.GroupMemberships.Add(new GroupMembership { TenantId = _tenantId, GroupId = team, UserId = bob });
            db.GroupMemberships.Add(new GroupMembership { TenantId = _tenantId, GroupId = team, UserId = outsider });
            await db.SaveChangesAsync();
        }

        await GrantReadAsync(repository, _userId);
        await GrantReadAsync(repository, bob);
        await DeclareAsync(store, _userId, manager: true);
        await DeclareAsync(store, groupId: team);

        // The outsider is in the group but reads nothing here: the intersection leaves them out.
        Assert.Equal(new[] { _userId, bob }.Order(), await SealedToAsync(store));
    }

    [Fact]
    public async Task A_personal_store_is_its_owners_alone_without_rows()
    {
        await SeedAsync();
        var space = await AddAsync($"Anna {Guid.NewGuid():N}", null, WellKnownMaskIds.Folder, personalOf: _userId);
        var store = await AddAsync("My Passwords", space, WellKnownMaskIds.PasswordStore);
        var bob = await PersonAsync("Bob");
        await GrantReadAsync(space, bob);

        Assert.Equal([_userId], await SealedToAsync(store));
        using var db = Ctx();
        Assert.False(await db.ContentAudienceMembers.AnyAsync(m => m.DocumentId == store));
    }

    [Fact]
    public async Task Leaving_the_audience_deletes_the_superseded_versions_their_key_opens_at_once()
    {
        using var kept = Certificate("kept");
        using var left = Certificate("left");
        var (repository, store) = await StoreAsync();
        var bob = await PersonAsync("Bob");
        await GrantReadAsync(repository, _userId);
        await GrantReadAsync(repository, bob);
        await DeclareAsync(store, _userId, manager: true);
        await EnrolAsync(_userId, kept, repository);
        await EnrolAsync(bob, left, repository);

        var entry = await AddAsync("Mail", store, WellKnownMaskIds.PasswordEntry);
        var now = DateTimeOffset.UtcNow;
        var first = await VersionAsync(entry, Sealed(kept, left), now.AddHours(-2), 1);

        // Bob is no longer in the audience (never declared here); a member re-seals to it: what Bob could open goes now.
        var resealed = await VersionAsync(entry, Sealed(kept), now.AddMinutes(-1), 2, VersionCommentKind.ResealedAfterAccessChange);
        using (var db = Ctx())
        {
            Assert.Equal(1, (await History(db).ReconcileAsync(entry, now, null, CancellationToken.None)).VersionsDeleted);
        }

        Assert.Equal([resealed], await VersionIdsAsync(entry));
        Assert.DoesNotContain(first, await VersionIdsAsync(entry));
    }

    [Fact]
    public async Task A_superseded_version_within_the_audience_keeps_its_undo_window()
    {
        using var kept = Certificate("kept");
        var (repository, store) = await StoreAsync();
        await GrantReadAsync(repository, _userId);
        await DeclareAsync(store, _userId, manager: true);
        await EnrolAsync(_userId, kept, repository);

        var entry = await AddAsync("Mail", store, WellKnownMaskIds.PasswordEntry);
        var now = DateTimeOffset.UtcNow;
        await VersionAsync(entry, Sealed(kept), now.AddHours(-2), 1);
        await VersionAsync(entry, Sealed(kept), now.AddMinutes(-1), 2);
        using var db = Ctx();
        Assert.Equal(0, (await History(db).ReconcileAsync(entry, now, null, CancellationToken.None)).VersionsDeleted);
    }

    // The host's modules: one that licenses enrolment, answered from the core registry only once a test ACTIVATES it
    // (EnrolAsync) — every other test here sees no module answer, as before ADR 0983.
    private sealed class LicensingModules : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IReadOnlyList<Alcifra.Infrastructure.Modules.ModuleLoader.LoadedModule>)
                ? new[] { new Alcifra.Infrastructure.Modules.ModuleLoader.LoadedModule(new LicensingModule(), "test://msg") }
                : null;
    }

    private sealed class LicensingModule : SimplArchive.ModuleAbi.IIndustryModule
    {
        public bool LicensesCertificateEnrolment => true;
        public string ModuleId => "msg";
        public string DisplayName => "Messenger";
        public int AbiMajorVersion => SimplArchive.ModuleAbi.ModuleAbiVersion.Major;
        public int AbiMinorVersion => SimplArchive.ModuleAbi.ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<SimplArchive.ModuleAbi.ModuleMaskSeed> Masks => [];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
    }

    // An attested enrolment as the registry holds it — the only certificate an audience member is sealed to (ADR 0963).
    private async Task EnrolAsync(Guid userId, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, Guid anchorParent)
    {
        var anchor = await AddAsync($"cert {Guid.NewGuid():N}", anchorParent, WellKnownMaskIds.Folder);
        using var db = Ctx();
        if (!await db.ModuleActivations.AnyAsync(a => a.ModuleId == "msg"))
        {
            db.ModuleActivations.Add(new Alcifra.Domain.Modules.ModuleActivation
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ModuleId = "msg",
                ActivatedAt = DateTimeOffset.UtcNow,
                SupportContractEndDate = DateTimeOffset.UtcNow.AddYears(1),
            });
            (await db.Tenants.SingleAsync(t => t.Id == _tenantId)).CertificateUnattestedHardwareAllowed = true;
        }

        db.EnrolledCertificates.Add(new Alcifra.Domain.Certificates.EnrolledCertificate
        {
            DocumentId = anchor,
            TenantId = _tenantId,
            UserId = userId,
            CertificatePem = certificate.ExportCertificatePem(),
            Label = "Key",
            Thumbprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
            Serial = certificate.SerialNumber,
            NotBefore = certificate.NotBefore,
            NotAfter = certificate.NotAfter,
        });
        await db.SaveChangesAsync();
    }
}
