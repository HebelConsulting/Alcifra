using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Acl;
using Alcifra.Infrastructure.Masks;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.ModuleAbi;

namespace Alcifra.IntegrationTests;

// A mask that names the file types it holds gets only those (ABI 1.29, ADR 1046), held at SaveChanges, so every door that
// adds a version row is covered: content arriving under the mask, and the mask arriving over content.
public class AcceptedContentTests
{
    private static readonly Guid ClipMask = Guid.Parse("0a550000-0000-0000-0000-000000000001");
    private static readonly Guid PlainMask = Guid.Parse("0a550000-0000-0000-0000-000000000002");
    private static readonly Guid ChannelMask = Guid.Parse("0a550000-0000-0000-0000-000000000003");
    private static readonly Guid LooseFolderMask = Guid.Parse("0a550000-0000-0000-0000-000000000004");
    private static readonly Guid ThumbMask = Guid.Parse("0a550000-0000-0000-0000-000000000005");
    private static readonly Guid PosterMask = Guid.Parse("0a550000-0000-0000-0000-000000000006");

    private sealed class ClipModule(IReadOnlyList<string>? accepted = null) : IIndustryModule
    {
        public string ModuleId => "clips";
        public string DisplayName => "Clips";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
        [
            new(ClipMask, "Clip", IsFolderMask: false, IsBookable: false, Fields: []) { AcceptedExtensions = accepted ?? ["mp4", ".MOV"] },
            new(PlainMask, "Plain", IsFolderMask: false, IsBookable: false, Fields: []),
            new(ChannelMask, "Channel", IsFolderMask: true, IsBookable: false, Fields: [], AdmitsOnlyDeclaredChildren: true, AdmittedChildren: [ClipMask, ThumbMask, PosterMask])
            {
                ReferencesObeyAdmission = true,
            },
            new(LooseFolderMask, "Loose", IsFolderMask: true, IsBookable: false, Fields: [], AdmitsOnlyDeclaredChildren: true, AdmittedChildren: [ClipMask]),
            new(ThumbMask, "Thumb", IsFolderMask: false, IsBookable: false, Fields: []),
            new(PosterMask, "Poster", IsFolderMask: false, IsBookable: false, Fields: [new ModuleFieldSeed("Thumbnail", "DocumentReference"), new ModuleFieldSeed("Note", "Text")]),
        ];

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

    private sealed record Rig(SqliteConnection Connection, Guid TenantId, Guid ClipVersion, Guid PlainVersion, Guid Owner, IModuleMaskDeclarations Masks)
    {
        public AlcifraDbContext Ctx() => new(
            new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(Connection).Options,
            new CurrentTenantAccessor { TenantId = TenantId },
            moduleMasks: Masks);

        public async Task<Guid> DocumentAsync(Guid maskVersion)
        {
            using var db = Ctx();
            var id = Guid.NewGuid();
            db.Documents.Add(new Document { Id = id, TenantId = TenantId, Name = $"{id:N}", MaskVersionId = maskVersion, CreatedByUserId = Owner, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task AddVersionAsync(Guid documentId, string key)
        {
            using var db = Ctx();
            db.DocumentVersions.Add(new DocumentVersion { Id = Guid.NewGuid(), DocumentId = documentId, TenantId = TenantId, Status = DocumentVersionStatus.Pending, ObjectKey = key, CreatedByUserId = Owner, CreatedAt = DateTimeOffset.UtcNow, DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow) });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var module = new ClipModule();
        var declarations = new ModuleMaskDeclarations([new ModuleLoader.LoadedModule(module, "test://clips")]);
        AlcifraDbContext Plain() => new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor { TenantId = tenantId });

        using (var setup = Plain())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            setup.Users.Add(new User { Id = owner, TenantId = tenantId, Email = "anna@x.io", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
            await new WellKnownMaskSeeder(setup, Microsoft.Extensions.Logging.Abstractions.NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
            await new ModuleMaskSeeder(setup, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
            await setup.SaveChangesAsync();
        }

        using var read = Plain();
        var clip = await read.MaskVersions.Where(v => v.MaskId == ClipMask && v.IsCurrent).Select(v => v.Id).SingleAsync();
        var plain = await read.MaskVersions.Where(v => v.MaskId == PlainMask && v.IsCurrent).Select(v => v.Id).SingleAsync();
        return new Rig(connection, tenantId, clip, plain, owner, declarations);
    }

    [Theory]
    [InlineData("tenants/t/2026/a/content.mp4")]
    [InlineData("tenants/t/2026/a/content.MP4")]
    [InlineData("tenants/t/2026/a/content.mov")]
    public async Task An_accepted_type_is_filed_in_any_case(string key)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        await rig.AddVersionAsync(await rig.DocumentAsync(rig.ClipVersion), key);
    }

    [Theory]
    [InlineData("tenants/t/2026/a/content.exe", "'.exe'")]
    [InlineData("tenants/t/2026/a/content.mp4.exe", "'.exe'")]
    [InlineData("tenants/t/2026/a/content", "without an extension")]
    public async Task Another_type_is_refused_naming_what_the_mask_holds(string key, string said)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var document = await rig.DocumentAsync(rig.ClipVersion);

        var refused = await Assert.ThrowsAsync<ContentExtensionNotAcceptedException>(() => rig.AddVersionAsync(document, key));
        Assert.Contains("A Clip holds .mov, .mp4", refused.Message, StringComparison.Ordinal);
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);

        using var db = rig.Ctx();
        Assert.Empty(await db.DocumentVersions.Where(v => v.DocumentId == document).ToListAsync());
    }

    [Fact]
    public async Task The_mask_cannot_be_assigned_over_content_it_does_not_hold_and_can_over_what_it_does()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var pdf = await rig.DocumentAsync(rig.PlainVersion);
        await rig.AddVersionAsync(pdf, "tenants/t/2026/a/content.pdf");
        var mov = await rig.DocumentAsync(rig.PlainVersion);
        await rig.AddVersionAsync(mov, "tenants/t/2026/b/content.mov");

        using var db = rig.Ctx();
        (await db.Documents.SingleAsync(d => d.Id == pdf)).MaskVersionId = rig.ClipVersion;
        await Assert.ThrowsAsync<ContentExtensionNotAcceptedException>(() => db.SaveChangesAsync());

        using var accepted = rig.Ctx();
        (await accepted.Documents.SingleAsync(d => d.Id == mov)).MaskVersionId = rig.ClipVersion;
        await accepted.SaveChangesAsync();
    }

    [Fact]
    public async Task A_mask_that_names_no_types_takes_anything()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        await rig.AddVersionAsync(await rig.DocumentAsync(rig.PlainVersion), "tenants/t/2026/a/content.exe");
    }

    private static async Task<Guid> VersionOfAsync(Rig rig, Guid mask)
    {
        using var db = rig.Ctx();
        return await db.MaskVersions.Where(v => v.MaskId == mask && v.IsCurrent).Select(v => v.Id).SingleAsync();
    }

    private static async Task PlaceAsync(Rig rig, Guid target, Guid folder)
    {
        using var db = rig.Ctx();
        db.DocumentReferences.Add(new DocumentReference { Id = Guid.NewGuid(), TenantId = rig.TenantId, ParentFolderId = folder, TargetDocumentId = target, CreatedByUserId = rig.Owner, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> PrincipalWithGrantOnAsync(Rig rig, params Guid[] documents)
    {
        using var db = rig.Ctx();
        var principal = await ModulePrincipal.EnsureAsync(db, "clips", "Clips", rig.TenantId, default);
        foreach (var document in documents)
        {
            db.AclEntries.Add(new Alcifra.Domain.Acl.AclEntry { Id = Guid.NewGuid(), TenantId = rig.TenantId, DocumentId = document, ServiceAccountId = principal.Id, CanSee = true, CanReadContent = true });
        }

        await db.SaveChangesAsync();
        return principal.Id;
    }

    private static async Task<bool> ReachesAsync(Rig rig, Guid principal, Guid document)
    {
        using var db = rig.Ctx();
        var reach = await ModuleReach.ForPrincipalAsync(db, new EffectiveRightsCalculator(db), rig.Masks.MasksOf("clips"), principal, [document], default);
        return reach.TryGetValue(document, out var r) && r.CanSee;
    }

    [Fact]
    public async Task A_folder_that_says_so_admits_a_reference_only_to_what_it_admits_as_a_child()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var channel = await rig.DocumentAsync(await VersionOfAsync(rig, ChannelMask));
        var loose = await rig.DocumentAsync(await VersionOfAsync(rig, LooseFolderMask));
        var clip = await rig.DocumentAsync(rig.ClipVersion);
        var plain = await rig.DocumentAsync(rig.PlainVersion);

        await PlaceAsync(rig, clip, channel);
        var refused = await Assert.ThrowsAsync<TypedFolderContainmentException>(() => PlaceAsync(rig, plain, channel));
        Assert.Contains("Channel", refused.Message, StringComparison.Ordinal);

        // Without the declaration a folder's references stay free, whatever it admits as children.
        await PlaceAsync(rig, plain, loose);
    }

    [Fact]
    public async Task The_facade_lists_a_folders_references_of_one_mask_oldest_first()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var channel = await rig.DocumentAsync(await VersionOfAsync(rig, ChannelMask));
        var (first, second) = (await rig.DocumentAsync(rig.ClipVersion), await rig.DocumentAsync(rig.ClipVersion));
        var thumb = await rig.DocumentAsync(await VersionOfAsync(rig, ThumbMask));
        await PlaceAsync(rig, first, channel);
        await PlaceAsync(rig, second, channel);
        await PlaceAsync(rig, thumb, channel);

        using var db = rig.Ctx();
        var facade = new ModuleArchiveFacade(db, new TestUserAccessor { UserId = rig.Owner }, new TestServiceAccountAccessor());
        var listed = await facade.GetReferencedAsync(channel, ClipMask);
        Assert.Equal([first, second], listed.Select(d => d.Id));
        Assert.All(listed, d => Assert.Equal(ClipMask, d.MaskId));
        Assert.All(listed, d => Assert.NotNull(d.ReferencedAt));

        Assert.Null(await facade.GetDocumentContentExtensionAsync(first));
        await rig.AddVersionAsync(first, "tenants/t/2026/a/content.MOV");
        using var fresh = rig.Ctx();
        var version = await fresh.DocumentVersions.SingleAsync(v => v.DocumentId == first);
        (version.Status, version.VersionNumber, version.Sha256Hash) = (DocumentVersionStatus.Confirmed, 1, "hash");
        await fresh.SaveChangesAsync();
        Assert.Equal("mov", await new ModuleArchiveFacade(fresh, new TestUserAccessor { UserId = rig.Owner }, new TestServiceAccountAccessor()).GetDocumentContentExtensionAsync(first));
    }

    [Fact]
    public async Task A_reference_in_a_folder_of_its_own_is_the_modules_invitation_and_removing_it_ends_it()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var channel = await rig.DocumentAsync(await VersionOfAsync(rig, ChannelMask));
        var unseen = await rig.DocumentAsync(await VersionOfAsync(rig, LooseFolderMask));
        var clip = await rig.DocumentAsync(rig.ClipVersion);
        var plain = await rig.DocumentAsync(rig.PlainVersion);
        var elsewhere = await rig.DocumentAsync(rig.ClipVersion);
        var principal = await PrincipalWithGrantOnAsync(rig, channel);

        Assert.False(await ReachesAsync(rig, principal, clip));
        await PlaceAsync(rig, clip, channel);
        Assert.True(await ReachesAsync(rig, principal, clip));

        // A folder it cannot see invites nothing, and another mask is never reached this way.
        await PlaceAsync(rig, elsewhere, unseen);
        Assert.False(await ReachesAsync(rig, principal, elsewhere));
        using (var db = rig.Ctx())
        {
            db.DocumentReferences.Add(new DocumentReference { Id = Guid.NewGuid(), TenantId = rig.TenantId, ParentFolderId = unseen, TargetDocumentId = plain, CreatedByUserId = rig.Owner, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.False(await ReachesAsync(rig, principal, plain));

        using (var db = rig.Ctx())
        {
            db.DocumentReferences.RemoveRange(await db.DocumentReferences.Where(r => r.TargetDocumentId == clip).ToListAsync());
            await db.SaveChangesAsync();
        }

        Assert.False(await ReachesAsync(rig, principal, clip));
    }

    [Fact]
    public async Task What_an_invited_document_names_in_a_reference_field_is_reached_and_a_text_field_invites_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var channel = await rig.DocumentAsync(await VersionOfAsync(rig, ChannelMask));
        var poster = await rig.DocumentAsync(await VersionOfAsync(rig, PosterMask));
        var thumb = await rig.DocumentAsync(await VersionOfAsync(rig, ThumbMask));
        var typed = await rig.DocumentAsync(await VersionOfAsync(rig, ThumbMask));
        var principal = await PrincipalWithGrantOnAsync(rig, channel);
        await PlaceAsync(rig, poster, channel);

        using (var db = rig.Ctx())
        {
            var maskVersion = await VersionOfAsync(rig, PosterMask);
            var fields = await db.FieldDefinitions.Where(f => f.MaskVersionId == maskVersion).ToListAsync();
            db.FieldValues.Add(new Alcifra.Domain.Masks.FieldValue { Id = Guid.NewGuid(), TenantId = rig.TenantId, DocumentId = poster, FieldDefinitionId = fields.Single(f => f.Name == "Thumbnail").Id, Value = thumb.ToString("D") });
            db.FieldValues.Add(new Alcifra.Domain.Masks.FieldValue { Id = Guid.NewGuid(), TenantId = rig.TenantId, DocumentId = poster, FieldDefinitionId = fields.Single(f => f.Name == "Note").Id, Value = typed.ToString("D") });
            await db.SaveChangesAsync();
        }

        Assert.True(await ReachesAsync(rig, principal, thumb));
        Assert.False(await ReachesAsync(rig, principal, typed));
    }

    [Theory]
    [InlineData]
    [InlineData("")]
    [InlineData("m p4")]
    [InlineData("tar.gz")]
    public void A_declaration_that_cannot_mean_anything_stops_the_module_loading(params string[] accepted) =>
        Assert.Throws<InvalidOperationException>(() => new ModuleMaskDeclarations([new ModuleLoader.LoadedModule(new ClipModule(accepted), "test://clips")]));
}
