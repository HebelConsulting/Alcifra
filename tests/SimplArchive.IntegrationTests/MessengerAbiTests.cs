using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Certificates;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// What the Messenger needs from the core (ABI 1.18, ADR 0937): its content in a segment of its own, a folder in every
// personal space with rights on that folder only, access and purge limited to its OWN kinds of document, and a user
// search that offers only people with a key. A module of this test's own, so the shared test module stays unchanged.
public class MessengerAbiTests
{
    private const string ModuleId = "msgr";
    private static readonly Guid MessagesMask = Guid.Parse("0e550000-0000-0000-0000-000000000001");
    private static readonly Guid GroupMask = Guid.Parse("0e550000-0000-0000-0000-000000000002");
    private static readonly Guid ObjectMask = Guid.Parse("0e550000-0000-0000-0000-000000000003");

    private sealed class MessengerModule : IIndustryModule
    {
        public string ModuleId => MessengerAbiTests.ModuleId;
        public string DisplayName => "Messenger";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
        [
            new(MessagesMask, "Messages", IsFolderMask: true, IsBookable: false, Fields: []) { PersonalSpaceFolderName = "My Messages" },
            new(GroupMask, "Message Group", IsFolderMask: true, IsBookable: false, Fields: []),
            new(ObjectMask, "Message Object", IsFolderMask: false, IsBookable: false, Fields: [])
            {
                ContentSegment = "messenger",
                Exemptions = ModuleMaskExemptions.All,
                ContentIsSealed = true,
            },
        ];

        public void ConfigureServices(IServiceCollection services) { }
    }

    private sealed class RecordingPurger(SqliteConnection connection, Guid tenantId) : IModuleDocumentPurger
    {
        public List<(Guid Id, bool RespectLegalHold)> Calls { get; } = [];

        public async Task<bool> PurgeAsync(Guid documentId, bool respectLegalHold, CancellationToken cancellationToken)
        {
            Calls.Add((documentId, respectLegalHold));
            using var db = Ctx(connection, tenantId);
            db.Documents.Remove(await db.Documents.SingleAsync(d => d.Id == documentId, cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    private sealed record Rig(
        ModuleArchiveFacade Facade, InMemoryObjectStorage Storage, Guid TenantId, Guid OwnerId, Guid OtherId, Guid RootId,
        RecordingPurger Purger, ServiceProvider Services, ModuleMaskDeclarations Declarations);

    private static SimplArchiveDbContext Ctx(SqliteConnection c, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        using (var seed = Ctx(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = ownerId, TenantId = tenantId, Email = "anna@x.io", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = otherId, TenantId = tenantId, Email = "tom@x.io", DisplayName = "Tom", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Messenger", CreatedByUserId = ownerId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        var module = new MessengerModule();
        using (var masks = Ctx(connection, tenantId))
        {
            await new WellKnownMaskSeeder(masks, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
            await new ModuleMaskSeeder(masks, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
            masks.ModuleActivations.Add(new ModuleActivation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ModuleId = ModuleId,
                SupportContractEndDate = DateTimeOffset.UtcNow.AddYears(1),
            });
            await masks.SaveChangesAsync();
            await ModulePrincipal.EnsureAsync(masks, ModuleId, "Messenger", tenantId, default);
        }

        var declarations = new ModuleMaskDeclarations([new ModuleLoader.LoadedModule(module, "test://msgr")]);
        var purger = new RecordingPurger(connection, tenantId);
        var services = new ServiceCollection()
            .AddSingleton<IReadOnlyList<ModuleLoader.LoadedModule>>([new ModuleLoader.LoadedModule(module, "test://msgr")])
            .AddSingleton<IModuleMaskDeclarations>(declarations)
            .AddSingleton<IModuleDocumentPurger>(purger)
            .BuildServiceProvider();
        var storage = new InMemoryObjectStorage();
        var facade = new ModuleArchiveFacade(
            Ctx(connection, tenantId), new TestUserAccessor { UserId = ownerId }, new TestServiceAccountAccessor(),
            identity: new ModuleIdentityAccessor { ModuleId = ModuleId }, objectStorage: storage, services: services);
        return new Rig(facade, storage, tenantId, ownerId, otherId, rootId, purger, services, declarations);
    }

    [Fact]
    public async Task A_message_object_keeps_its_content_in_the_modules_segment()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var group = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "Team");

        var file = await rig.Facade.CreateContentDocumentAsync(group, ObjectMask, "photo.p7m", Sealed(), ".p7m");

        using var check = Ctx(connection, rig.TenantId);
        var key = await check.DocumentVersions.Where(v => v.DocumentId == file).Select(v => v.ObjectKey).SingleAsync();
        Assert.StartsWith($"tenants/{rig.TenantId}/messenger/", key);
        Assert.EndsWith(".p7m", key);
        Assert.True(rig.Storage.Objects.ContainsKey(key));
        Assert.True(rig.Declarations.IsUnrenderedContent(key));   // what previews and text recognition ask
    }

    [Fact]
    public async Task Access_is_granted_and_revoked_on_the_modules_own_documents_and_refused_on_others()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var group = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "Team");

        await rig.Facade.GrantAccessAsync(group, rig.OtherId, ModuleAccess.Read);
        using (var check = Ctx(connection, rig.TenantId))
        {
            var entry = await check.AclEntries.SingleAsync(e => e.DocumentId == group && e.UserId == rig.OtherId);
            Assert.True(entry.CanSee && entry.CanReadContent && !entry.CanCreateSubItems && !entry.CanManagePermissions);
        }

        await rig.Facade.RevokeAccessAsync(group, rig.OtherId);
        using (var check = Ctx(connection, rig.TenantId))
        {
            Assert.False(await check.AclEntries.AnyAsync(e => e.DocumentId == group && e.UserId == rig.OtherId));
        }

        // The root wears no mask of the module's: deciding who reads it is not the module's business.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Facade.GrantAccessAsync(rig.RootId, rig.OtherId, ModuleAccess.Read));
    }

    [Fact]
    public async Task Deleting_purges_at_once_ignoring_holds_for_an_exempt_mask_and_refuses_foreign_documents()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var group = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "Team");
        var file = await rig.Facade.CreateContentDocumentAsync(group, ObjectMask, "a.p7m", Sealed(), ".p7m");

        await rig.Facade.DeleteDocumentAsync(file);

        Assert.Equal((file, false), Assert.Single(rig.Purger.Calls));   // exempt from legal hold: holds are not consulted
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Facade.DeleteDocumentAsync(rig.RootId));
    }

    [Fact]
    public async Task Delete_for_me_removes_only_the_reference()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var shared = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "Direct");
        var mine = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "Mine");
        var file = await rig.Facade.CreateContentDocumentAsync(shared, ObjectMask, "a.p7m", Sealed(), ".p7m");
        await rig.Facade.CreateReferenceAsync(file, mine);

        Assert.True(await rig.Facade.DeleteReferenceAsync(file, mine));
        Assert.False(await rig.Facade.DeleteReferenceAsync(file, mine));

        using var check = Ctx(connection, rig.TenantId);
        Assert.True(await check.Documents.AnyAsync(d => d.Id == file));   // the file itself stays
    }

    [Fact]
    public async Task User_search_offers_only_people_with_a_usable_enrolled_key()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        using (var db = Ctx(connection, rig.TenantId))
        {
            var certificateDoc = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "cert");
            db.EnrolledCertificates.Add(new EnrolledCertificate
            {
                DocumentId = certificateDoc,
                TenantId = rig.TenantId,
                UserId = rig.OtherId,
                CertificatePem = "pem",
                Label = "Card",
                Thumbprint = "AB",
                Serial = "01",
                NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
                NotAfter = DateTimeOffset.UtcNow.AddYears(1),
            });
            await db.SaveChangesAsync();
        }

        var everyone = await rig.Facade.SearchUsersAsync(string.Empty);
        var byMail = await rig.Facade.SearchUsersAsync("TOM@");

        Assert.Equal([rig.OtherId], everyone.Select(u => u.Id));   // Anna has no key, so she is not offered
        Assert.Equal("Tom", Assert.Single(byMail).DisplayName);
    }

    [Fact]
    public async Task My_Messages_is_provisioned_once_per_personal_space_with_rights_on_that_folder_only()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        Guid personalRoot;
        using (var db = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = rig.TenantId }, moduleMasks: rig.Declarations))
        {
            var root = await new PersonalRepositoryProvisioner(db, new NoOpAudit(), new ModulePersonalFolders(db, rig.Declarations))
                .EnsureAsync(rig.OwnerId, rig.TenantId, default);
            personalRoot = root.Id;
            await new ModulePersonalFolders(db, rig.Declarations).EnsureForTenantAsync(rig.TenantId, default);   // a second run changes nothing
        }

        using var check = Ctx(connection, rig.TenantId);
        var folder = await check.Documents.SingleAsync(d => d.ParentId == personalRoot && d.Name == "My Messages");
        var principal = (await ModulePrincipal.FindAsync(check, ModuleId, default))!.Id;
        var entry = await check.AclEntries.SingleAsync(e => e.ServiceAccountId == principal);
        Assert.Equal(folder.Id, entry.DocumentId);                   // on that folder, nowhere else
        Assert.False(entry.CanManagePermissions);
        Assert.False(await check.AclEntries.AnyAsync(e => e.DocumentId == personalRoot && e.ServiceAccountId == principal));
    }

    [Fact]
    public async Task The_module_finds_each_users_own_provisioned_folder_and_nothing_else()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        using (var db = new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = rig.TenantId }, moduleMasks: rig.Declarations))
        {
            var folders = new ModulePersonalFolders(db, rig.Declarations);
            await new PersonalRepositoryProvisioner(db, new NoOpAudit(), folders).EnsureAsync(rig.OwnerId, rig.TenantId, default);
        }

        var annas = await rig.Facade.GetPersonalSpaceFolderAsync(rig.OwnerId, MessagesMask);
        var toms = await rig.Facade.GetPersonalSpaceFolderAsync(rig.OtherId, MessagesMask);

        using var check = Ctx(connection, rig.TenantId);
        Assert.Equal("My Messages", (await check.Documents.SingleAsync(d => d.Id == annas)).Name);
        Assert.Null(toms);   // Tom has no personal space yet
        // A mask that is not provisioned in personal spaces is no way to look around in one.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Facade.GetPersonalSpaceFolderAsync(rig.OwnerId, GroupMask));
    }

    [Fact]
    public async Task Content_declared_sealed_is_filed_only_as_an_envelope_and_a_refusal_leaves_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var group = await rig.Facade.CreateDocumentAsync(rig.RootId, GroupMask, "Team");
        var file = await rig.Facade.CreateContentDocumentAsync(group, ObjectMask, "a.p7m", Sealed(), ".p7m");
        var stored = rig.Storage.Objects.Count;

        await Assert.ThrowsAsync<ModuleContentNotSealedException>(() =>
            rig.Facade.CreateContentDocumentAsync(group, ObjectMask, "b.p7m", Encoding.UTF8.GetBytes("plain words"), ".p7m"));
        await Assert.ThrowsAsync<ModuleContentNotSealedException>(() =>   // a new version is checked against the document's mask
            rig.Facade.CreateContentDocumentAsync(group, GroupMask, "a.p7m", [0x30, 0x03, 0x06, 0x01, 0x00], ".p7m", replaceDocumentId: file));
        await Assert.ThrowsAsync<ModuleContentNotSealedException>(() =>   // an envelope with plaintext riding behind it
            rig.Facade.CreateContentDocumentAsync(group, ObjectMask, "c.p7m", [.. Sealed(), .. Encoding.UTF8.GetBytes("tail")], ".p7m"));

        Assert.Equal(stored, rig.Storage.Objects.Count);
        using var check = Ctx(connection, rig.TenantId);
        Assert.Equal(1, await check.DocumentVersions.CountAsync(v => v.DocumentId == file));
        Assert.False(await check.Documents.AnyAsync(d => d.Name == "b.p7m" || d.Name == "c.p7m"));
        // An unsealed mask files anything, as before.
        await rig.Facade.CreateContentDocumentAsync(group, GroupMask, "notes", Encoding.UTF8.GetBytes("plain words"), ".txt");
    }

    [Fact]
    public void Sealed_content_needs_a_segment_of_its_own()
    {
        Assert.Throws<InvalidOperationException>(() => Declare(new ModuleMaskSeed(Guid.NewGuid(), "X", false, false, []) { ContentIsSealed = true }));
        Assert.Throws<InvalidOperationException>(() => Declare(
            new ModuleMaskSeed(Guid.NewGuid(), "X", false, false, []) { ContentSegment = "shared", ContentIsSealed = true },
            new ModuleMaskSeed(Guid.NewGuid(), "Y", false, false, []) { ContentSegment = "shared" }));

        var declarations = Declare(new ModuleMaskSeed(ObjectMask, "X", false, false, []) { ContentSegment = "sealed", ContentIsSealed = true });
        Assert.Contains(ObjectMask, declarations.SealedContent);
        Assert.True(declarations.IsSealedContent($"tenants/{Guid.NewGuid()}/sealed/{Guid.NewGuid()}/content.p7m"));
        Assert.False(declarations.IsSealedContent($"tenants/{Guid.NewGuid()}/2026/{Guid.NewGuid()}/content.p7m"));
    }

    private static ModuleMaskDeclarations Declare(params ModuleMaskSeed[] masks) =>
        new([new ModuleLoader.LoadedModule(new DeclaringModule(masks), "test://declare")]);

    private sealed class DeclaringModule(IReadOnlyList<ModuleMaskSeed> masks) : IIndustryModule
    {
        public string ModuleId => "declare";
        public string DisplayName => "Declare";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => masks;
        public void ConfigureServices(IServiceCollection services) { }
    }

    private static byte[] Sealed()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=reader", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var envelope = new EnvelopedCms(new ContentInfo(Encoding.UTF8.GetBytes("for the readers only")));
        envelope.Encrypt(new CmsRecipient(certificate));
        return envelope.Encode();
    }

    [Fact]
    public void A_content_segment_that_could_mix_with_the_cores_areas_stops_the_start()
    {
        foreach (var segment in new[] { "2026", "users", "Messenger", "a" })
        {
            var module = new BadSegmentModule(segment);
            Assert.Throws<InvalidOperationException>(() => new ModuleMaskDeclarations([new ModuleLoader.LoadedModule(module, "test://bad")]));
        }
    }

    private sealed class BadSegmentModule(string segment) : IIndustryModule
    {
        public string ModuleId => "bad";
        public string DisplayName => "Bad";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [new(Guid.NewGuid(), "X", false, false, []) { ContentSegment = segment }];
        public void ConfigureServices(IServiceCollection services) { }
    }

    private sealed class TestUserAccessor : ICurrentUserAccessor
    {
        public Guid? UserId { get; set; }
    }

    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor
    {
        public Guid? ServiceAccountId { get; set; }
    }

    private sealed class NoOpAudit : IAuditRecorder
    {
        public Task RecordAsync(string action, string? targetType = null, Guid? targetId = null, string? targetName = null,
            string? details = null, Guid? tenantId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordForActorAsync(Domain.Audit.AuditActorType actorType, Guid actorId, string actorName, Guid tenantId, string action,
            string? targetType = null, Guid? targetId = null, string? targetName = null, string? details = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
