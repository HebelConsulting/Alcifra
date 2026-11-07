using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using Alcifra.ModuleAbi;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alcifra.IntegrationTests;

// A document's mask may not change while a folder binding its references, or a reference field naming the masks it
// accepts, holds it and would not take the new one (#2214, ADR 1074). An ordinary reference or field holds nothing.
public class MaskHeldByReferenceTests
{
    private static readonly Guid PlaylistMask = Guid.Parse("0a560000-0000-0000-0000-000000000001");
    private static readonly Guid VideoMask = Guid.Parse("0a560000-0000-0000-0000-000000000002");
    private static readonly Guid ShortMask = Guid.Parse("0a560000-0000-0000-0000-000000000003");
    private static readonly Guid ThumbMask = Guid.Parse("0a560000-0000-0000-0000-000000000004");
    private static readonly Guid PlainMask = Guid.Parse("0a560000-0000-0000-0000-000000000005");
    private static readonly Guid LooseFolderMask = Guid.Parse("0a560000-0000-0000-0000-000000000006");

    private sealed class TubeModule : IIndustryModule
    {
        public string ModuleId => "tube-test";
        public string DisplayName => "Tube test";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;

        public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
        [
            new(PlaylistMask, "Playlist", IsFolderMask: true, IsBookable: false, Fields: [], AdmitsOnlyDeclaredChildren: true, AdmittedChildren: [VideoMask, ShortMask])
            {
                ReferencesObeyAdmission = true,
            },
            new(LooseFolderMask, "Loose", IsFolderMask: true, IsBookable: false, Fields: [], AdmitsOnlyDeclaredChildren: true, AdmittedChildren: [VideoMask]),
            new(VideoMask, "Video", IsFolderMask: false, IsBookable: false, Fields:
            [
                new ModuleFieldSeed("Thumbnail", "DocumentReference") { AcceptedMasks = [ThumbMask] },
                new ModuleFieldSeed("See also", "DocumentReference"),
            ]),
            new(ShortMask, "Short", IsFolderMask: false, IsBookable: false, Fields: []),
            new(ThumbMask, "Thumb", IsFolderMask: false, IsBookable: false, Fields: []),
            new(PlainMask, "Plain", IsFolderMask: false, IsBookable: false, Fields: []),
        ];

        public void ConfigureServices(IServiceCollection services) { }
    }

    private sealed record Rig(SqliteConnection Connection, Guid TenantId, Guid Owner, IModuleMaskDeclarations Masks)
    {
        public AlcifraDbContext Ctx() => new(
            new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(Connection).Options,
            new CurrentTenantAccessor { TenantId = TenantId },
            moduleMasks: Masks);

        public async Task<Guid> VersionOfAsync(Guid mask)
        {
            using var db = Ctx();
            return await db.MaskVersions.Where(v => v.MaskId == mask && v.IsCurrent).Select(v => v.Id).SingleAsync();
        }

        public async Task<Guid> DocumentAsync(Guid mask, string name)
        {
            var version = await VersionOfAsync(mask);
            using var db = Ctx();
            var id = Guid.NewGuid();
            db.Documents.Add(new Document { Id = id, TenantId = TenantId, Name = name, MaskVersionId = version, CreatedByUserId = Owner, CreatedAt = DateTimeOffset.UtcNow, StorageFolderId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task PlaceAsync(Guid target, Guid folder)
        {
            using var db = Ctx();
            db.DocumentReferences.Add(new DocumentReference { Id = Guid.NewGuid(), TenantId = TenantId, ParentFolderId = folder, TargetDocumentId = target, CreatedByUserId = Owner, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        public async Task NameAsync(Guid holder, string field, Guid target)
        {
            var version = await VersionOfAsync(VideoMask);
            using var db = Ctx();
            var definition = await db.FieldDefinitions.SingleAsync(f => f.MaskVersionId == version && f.Name == field);
            db.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = TenantId, DocumentId = holder, FieldDefinitionId = definition.Id, Value = target.ToString("D") });
            await db.SaveChangesAsync();
        }

        public async Task RemaskAsync(Guid document, Guid mask)
        {
            var version = await VersionOfAsync(mask);
            using var db = Ctx();
            (await db.Documents.SingleAsync(d => d.Id == document)).MaskVersionId = version;
            await db.SaveChangesAsync();
        }

        public async Task<Guid> MaskOfAsync(Guid document)
        {
            using var db = Ctx();
            return await db.Documents.Where(d => d.Id == document)
                .Join(db.MaskVersions, d => d.MaskVersionId, v => v.Id, (_, v) => v.MaskId).SingleAsync();
        }
    }

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var module = new TubeModule();
        var declarations = new ModuleMaskDeclarations([new ModuleLoader.LoadedModule(module, "test://tube")]);
        using (var setup = new AlcifraDbContext(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor { TenantId = tenantId }))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            setup.Users.Add(new User { Id = owner, TenantId = tenantId, Email = "anna@x.io", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
            await new WellKnownMaskSeeder(setup, Microsoft.Extensions.Logging.Abstractions.NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
            await new ModuleMaskSeeder(setup, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
            await setup.SaveChangesAsync();
        }

        return new Rig(connection, tenantId, owner, declarations);
    }

    [Fact]
    public async Task A_video_in_a_playlist_cannot_become_what_the_playlist_does_not_admit()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var playlist = await rig.DocumentAsync(PlaylistMask, "Holidays");
        var video = await rig.DocumentAsync(VideoMask, "Beach");
        await rig.PlaceAsync(video, playlist);

        var refused = await Assert.ThrowsAsync<MaskHeldByReferenceException>(() => rig.RemaskAsync(video, PlainMask));
        Assert.Contains("'Beach' cannot become a 'Plain'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'Holidays'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(VideoMask, await rig.MaskOfAsync(video));

        // What the playlist admits too is no change to it.
        await rig.RemaskAsync(video, ShortMask);
    }

    [Fact]
    public async Task Taking_it_out_of_the_playlist_in_the_same_save_lets_the_mask_change()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var playlist = await rig.DocumentAsync(PlaylistMask, "Holidays");
        var video = await rig.DocumentAsync(VideoMask, "Beach");
        await rig.PlaceAsync(video, playlist);

        var plain = await rig.VersionOfAsync(PlainMask);
        using var db = rig.Ctx();
        db.DocumentReferences.RemoveRange(await db.DocumentReferences.Where(r => r.TargetDocumentId == video).ToListAsync());
        (await db.Documents.SingleAsync(d => d.Id == video)).MaskVersionId = plain;
        await db.SaveChangesAsync();
        Assert.Equal(PlainMask, await rig.MaskOfAsync(video));
    }

    [Fact]
    public async Task A_thumbnail_named_by_a_field_that_accepts_only_thumbnails_keeps_its_mask()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var video = await rig.DocumentAsync(VideoMask, "Beach");
        var thumb = await rig.DocumentAsync(ThumbMask, "Still");
        await rig.NameAsync(video, "Thumbnail", thumb);

        var refused = await Assert.ThrowsAsync<MaskHeldByReferenceException>(() => rig.RemaskAsync(thumb, PlainMask));
        Assert.Contains("'Still' cannot become a 'Plain'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("field 'Thumbnail' of 'Beach'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_reference_or_an_open_field_holds_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        var rig = await RigAsync(connection);
        var loose = await rig.DocumentAsync(LooseFolderMask, "Loose");
        var video = await rig.DocumentAsync(VideoMask, "Beach");
        var named = await rig.DocumentAsync(VideoMask, "Dunes");
        await rig.PlaceAsync(video, loose);
        await rig.NameAsync(video, "See also", named);

        await rig.RemaskAsync(video, PlainMask);
        await rig.RemaskAsync(named, PlainMask);
        Assert.Equal(PlainMask, await rig.MaskOfAsync(named));
    }
}
