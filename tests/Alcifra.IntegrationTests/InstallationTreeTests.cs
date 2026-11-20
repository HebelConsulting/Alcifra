using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Api.Manuals;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Localization;

namespace Alcifra.IntegrationTests;

// The core's own tree (ADR 1095, #1893): the container folder named after the product, directly under the standard
// repository, holding the manuals folder and the Installation folder — created and healed idempotently at startup, with
// the shape every installation had before it (manuals loose under the repository, the keys wearing the Manual mask)
// reached from that shape and no other.
public class InstallationTreeTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private AlcifraDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(c).Options, a);

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId, Guid StandardId, Guid OtherId)> TenantAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor { TenantId = _tenantId };
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        using var db = Ctx(connection, accessor);
        var tenant = new Tenant { Id = _tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(tenant);
        var user = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);

        var standard = Root("Standard", user.Id);
        var other = Root("Other", user.Id);
        db.Documents.AddRange(standard, other);
        await db.SaveChangesAsync();
        tenant.StandardRepositoryId = standard.Id;
        await db.SaveChangesAsync();
        return (connection, accessor, user.Id, standard.Id, other.Id);
    }

    private Document Root(string name, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        Name = name,
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Task<Guid> MaskVersionAsync(AlcifraDbContext db, Guid maskId) =>
        db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).SingleAsync();

    // The shape before ADR 1095: the manuals folder directly under the standard repository.
    private async Task<Document> LegacyManualsFolderAsync(AlcifraDbContext db, Guid parentId, Guid userId, string? name = null)
    {
        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = parentId,
            Name = name ?? CoreOwnedNames.ManualsFolder,
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Documentation),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PermitCoreOwnedMask(folder.Id);
        db.Documents.Add(folder);
        return folder;
    }

    // The shape before ADR 1095: the archive signing keys inside the manuals folder, wearing the Manual mask and found
    // by their Article number.
    private async Task<Document> LegacySigningKeysAsync(AlcifraDbContext db, Guid parentId, Guid userId)
    {
        var manualMaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Manual);
        var keys = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = parentId,
            Name = "Archive signing keys",
            MaskVersionId = manualMaskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(keys);
        var article = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == manualMaskVersionId && f.Name == WellKnownMaskIds.ManualFields.ArticleNumber)
            .Select(f => f.Id).SingleAsync();
        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DocumentId = keys.Id,
            FieldDefinitionId = article,
            Value = ManualFiler.ArchiveSigningKeysArticleNumber,
        });
        return keys;
    }

    private async Task<Document> TreeAsync(AlcifraDbContext db, Guid maskId) =>
        await db.Documents.AsNoTracking().SingleAsync(d =>
            db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == maskId));

    [Fact]
    public async Task The_tree_is_created_under_the_standard_repository_and_holding_the_installation_folder()
    {
        var (connection, accessor, _, standardId, _) = await TenantAsync();
        using var _c = connection;
        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            var container = await TreeAsync(db, WellKnownMaskIds.CoreFolder);
            var installation = await TreeAsync(db, WellKnownMaskIds.Installation);
            Assert.Equal(standardId, container.ParentId);
            Assert.Equal(CoreOwnedNames.CoreFolder, container.Name);
            Assert.Equal(container.Id, installation.ParentId);
            Assert.Equal(CoreOwnedNames.InstallationFolder, installation.Name);
        }
    }

    [Fact]
    public async Task The_shape_before_the_container_is_reached_and_running_again_changes_nothing()
    {
        var (connection, accessor, userId, standardId, _) = await TenantAsync();
        using var _c = connection;

        Guid manualsId, keysId;
        using (var db = Ctx(connection, accessor))
        {
            var manuals = await LegacyManualsFolderAsync(db, standardId, userId, $"{PersistedNames.EarlierProduct} Manuals");
            keysId = (await LegacySigningKeysAsync(db, manuals.Id, userId)).Id;
            manualsId = manuals.Id;
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
        }

        Guid containerId, installationId;
        using (var db = Ctx(connection, accessor))
        {
            var container = await TreeAsync(db, WellKnownMaskIds.CoreFolder);
            var installation = await TreeAsync(db, WellKnownMaskIds.Installation);
            containerId = container.Id;
            installationId = installation.Id;

            // The manuals folder is inside the container, the keys inside the Installation folder — wearing the Public
            // keys mask, with its Purpose and none of the Manual field values it arrived with.
            var manuals = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == manualsId);
            Assert.Equal(container.Id, manuals.ParentId);
            var keys = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == keysId);
            Assert.Equal(installation.Id, keys.ParentId);
            Assert.Equal(await MaskVersionAsync(db, WellKnownMaskIds.PublicKeys), keys.MaskVersionId);
            var values = await db.FieldValues.AsNoTracking()
                .Where(v => v.DocumentId == keysId)
                .Join(db.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value })
                .ToListAsync();
            var purpose = Assert.Single(values);
            Assert.Equal(WellKnownMaskIds.PublicKeyFields.Purpose, purpose.Name);
            Assert.Equal(ManualFiler.ArchiveSigningPurpose, purpose.Value);
        }

        // Idempotent: the found shape is the marker that the migration ran, so a second start touches nothing.
        var before = await DocumentCountAsync(connection, accessor);
        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
        }

        Assert.Equal(before, await DocumentCountAsync(connection, accessor));
        using (var db = Ctx(connection, accessor))
        {
            Assert.Equal(containerId, (await TreeAsync(db, WellKnownMaskIds.CoreFolder)).Id);
            Assert.Equal(installationId, (await TreeAsync(db, WellKnownMaskIds.Installation)).Id);
            Assert.Equal(containerId, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == manualsId)).ParentId);
        }
    }

    [Fact]
    public async Task A_manuals_folder_a_person_moved_out_of_the_container_stays_where_they_put_it()
    {
        var (connection, accessor, _, _, otherId) = await TenantAsync();
        using var _c = connection;

        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
        }

        Guid folderId;
        using (var db = Ctx(connection, accessor))
        {
            // The manuals folder, created by the filer into the container, then moved by a person into another repository.
            var container = await TreeAsync(db, WellKnownMaskIds.CoreFolder);
            var folder = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = container.Id,
                Name = "Manuals",
                MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Documentation),
                CreatedByUserId = (await db.Documents.SingleAsync(d => d.Id == container.Id)).CreatedByUserId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.PermitCoreOwnedMask(folder.Id);
            db.Documents.Add(folder);
            await db.SaveChangesAsync();
            folderId = folder.Id;

            // It is deliberately NOT protected (ADR 1066's note): a person may move it.
            (await db.Documents.SingleAsync(d => d.Id == folderId)).ParentId = otherId;
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
        }

        using (var db = Ctx(connection, accessor))
        {
            Assert.Equal(otherId, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == folderId)).ParentId);
        }
    }

    [Fact]
    public async Task The_container_and_the_installation_folder_are_protected_but_the_cores_own_move_is_granted()
    {
        var (connection, accessor, _, standardId, otherId) = await TenantAsync();
        using var _c = connection;

        Guid containerId, installationId;
        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
            containerId = (await TreeAsync(db, WellKnownMaskIds.CoreFolder)).Id;
            installationId = (await TreeAsync(db, WellKnownMaskIds.Installation)).Id;
        }

        foreach (var (id, change) in new (Guid, Action<Document>)[]
                 {
                     (containerId, d => d.Name = "Mine"),
                     (containerId, d => d.ParentId = otherId),
                     (containerId, d => d.DeletedAt = DateTimeOffset.UtcNow),
                     (installationId, d => d.Name = "Mine"),
                 })
        {
            using var db = Ctx(connection, accessor);
            change(await db.Documents.SingleAsync(d => d.Id == id));
            await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        }

        // The one move the core performs (ADR 1095): granted, saved, and the grant spent — an ungranted second move
        // is refused again.
        using (var db = Ctx(connection, accessor))
        {
            var container = await db.Documents.SingleAsync(d => d.Id == containerId);
            db.PermitCoreOwnedFolderMove(container.Id);
            container.ParentId = otherId;
            await db.SaveChangesAsync();
            Assert.Equal(otherId, container.ParentId);
        }

        using (var db = Ctx(connection, accessor))
        {
            (await db.Documents.SingleAsync(d => d.Id == containerId)).ParentId = standardId;
            await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Changing_the_standard_repository_moves_the_container_in_the_same_save()
    {
        var (connection, accessor, userId, standardId, otherId) = await TenantAsync();
        using var _c = connection;

        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
            var tenant = await db.Tenants.SingleAsync(t => t.Id == _tenantId);
            tenant.StandardRepositoryId = otherId;
            await StandardRepositoryChange.MoveUnderAsync(db, _tenantId, otherId, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            var container = await TreeAsync(db, WellKnownMaskIds.CoreFolder);
            Assert.Equal(otherId, container.ParentId);
            // …and its children came with it, because the container is what moved.
            var installation = await TreeAsync(db, WellKnownMaskIds.Installation);
            Assert.Equal(container.Id, installation.ParentId);
        }

        GC.KeepAlive((standardId, userId));
    }

    [Fact]
    public async Task A_container_moved_into_a_repository_where_its_name_is_taken_takes_the_next_free_name()
    {
        var (connection, accessor, userId, _, otherId) = await TenantAsync();
        using var _c = connection;

        Guid containerId;
        using (var db = Ctx(connection, accessor))
        {
            await InstallationTree.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);
            containerId = (await TreeAsync(db, WellKnownMaskIds.CoreFolder)).Id;

            // A person's own folder already carries the container's name in the repository it is about to move into.
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                ParentId = otherId,
                Name = CoreOwnedNames.CoreFolder,
                MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Folder),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            (await db.Tenants.SingleAsync(t => t.Id == _tenantId)).StandardRepositoryId = otherId;
            await StandardRepositoryChange.MoveUnderAsync(db, _tenantId, otherId, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using (var db = Ctx(connection, accessor))
        {
            var container = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == containerId);
            Assert.Equal(otherId, container.ParentId);
            Assert.Equal($"{CoreOwnedNames.CoreFolder} (2)", container.Name);
        }
    }

    private async Task<int> DocumentCountAsync(SqliteConnection connection, CurrentTenantAccessor accessor)
    {
        using var db = Ctx(connection, accessor);
        return await db.Documents.CountAsync();
    }
}
