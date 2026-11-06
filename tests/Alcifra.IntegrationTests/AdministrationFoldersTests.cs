using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// Administration and Users as real folders held by the core (ADR 1066, #2180): created once per tenant and found by mask,
// Users referencing exactly the live personal spaces, and every change a person could make to them refused at SaveChanges.
public class AdministrationFoldersTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private AlcifraDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(c).Options, a);

    private async Task<(SqliteConnection Connection, CurrentTenantAccessor Accessor, Guid UserId, Guid SpaceId)> TenantAsync()
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

        var standard = Root("Standard", user.Id, await MaskVersionAsync(db, WellKnownMaskIds.Repository));
        var space = Root("U (T)", user.Id, await MaskVersionAsync(db, WellKnownMaskIds.UserFolder));
        space.PersonalOfUserId = user.Id;
        db.Documents.AddRange(standard, space);
        await db.SaveChangesAsync();
        tenant.StandardRepositoryId = standard.Id;
        await db.SaveChangesAsync();
        return (connection, accessor, user.Id, space.Id);
    }

    private Document Root(string name, Guid userId, Guid maskVersionId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        Name = name,
        MaskVersionId = maskVersionId,
        CreatedByUserId = userId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Task<Guid> MaskVersionAsync(AlcifraDbContext db, Guid maskId) =>
        db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).SingleAsync();

    private Task EnsureAsync(AlcifraDbContext db) => AdministrationFolders.EnsureAsync(db, _tenantId, NullLogger.Instance, CancellationToken.None);

    private async Task<(Document Root, Document Users)> FoldersAsync(AlcifraDbContext db) =>
        ((await AdministrationFolders.FindAsync(db, _tenantId, WellKnownMaskIds.Administration, CancellationToken.None))!,
         (await AdministrationFolders.FindAsync(db, _tenantId, WellKnownMaskIds.Users, CancellationToken.None))!);

    [Fact]
    public async Task One_root_with_users_beneath_referencing_every_personal_space_and_a_second_run_changes_nothing()
    {
        var (connection, accessor, _, spaceId) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        await EnsureAsync(db);
        var (root, users) = await FoldersAsync(db);
        Assert.Null(root.ParentId);
        Assert.Equal(AdministrationFolders.RootName, root.Name);
        Assert.Equal(root.Id, users.ParentId);
        var reference = Assert.Single(await db.DocumentReferences.Where(r => r.ParentFolderId == users.Id).ToListAsync());
        Assert.Equal(spaceId, reference.TargetDocumentId);

        await EnsureAsync(db);
        Assert.Equal(1, await db.Documents.CountAsync(d => d.MaskVersionId == root.MaskVersionId));
        Assert.Equal(1, await db.DocumentReferences.CountAsync(r => r.ParentFolderId == users.Id));
    }

    [Fact]
    public async Task A_new_space_is_referenced_at_once_and_a_vanished_one_is_dropped_at_the_next_run()
    {
        var (connection, accessor, userId, spaceId) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);
        await EnsureAsync(db);
        var (_, users) = await FoldersAsync(db);

        var other = new User { Id = Guid.NewGuid(), TenantId = _tenantId, Email = "o@t.test", DisplayName = "O", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(other);
        var second = Root("O (T)", other.Id, await MaskVersionAsync(db, WellKnownMaskIds.UserFolder));
        second.PersonalOfUserId = other.Id;
        db.Documents.Add(second);
        await db.SaveChangesAsync();
        await AdministrationFolders.AddUserReferenceAsync(db, _tenantId, second.Id, CancellationToken.None);
        Assert.Equal(2, await db.DocumentReferences.CountAsync(r => r.ParentFolderId == users.Id));

        // The first space goes (hard-deleted here; a soft delete keeps the row and so the reference, which listings hide).
        db.Documents.Remove(await db.Documents.SingleAsync(d => d.Id == spaceId));
        db.PermitCoreOwnedReference((await db.DocumentReferences.SingleAsync(r => r.TargetDocumentId == spaceId)).Id);
        db.DocumentReferences.Remove(await db.DocumentReferences.SingleAsync(r => r.TargetDocumentId == spaceId));
        await db.SaveChangesAsync();
        await EnsureAsync(db);
        Assert.Equal(second.Id, Assert.Single(await db.DocumentReferences.Where(r => r.ParentFolderId == users.Id).ToListAsync()).TargetDocumentId);
        _ = userId;
    }

    [Fact]
    public async Task The_folders_are_not_deleted_renamed_or_moved()
    {
        var (connection, accessor, _, _) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);
        await EnsureAsync(db);
        var (root, users) = await FoldersAsync(db);

        root.DeletedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        (root, users) = await FoldersAsync(db);
        db.Documents.Remove(users);
        await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        (root, users) = await FoldersAsync(db);
        users.Name = "People";
        await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        (root, users) = await FoldersAsync(db);
        var standardId = await db.Tenants.Select(t => t.StandardRepositoryId).SingleAsync();
        users.ParentId = standardId;
        await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        _ = root;
    }

    [Fact]
    public async Task Nothing_is_placed_in_users_by_hand_and_the_root_admits_only_users()
    {
        var (connection, accessor, userId, spaceId) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);
        await EnsureAsync(db);
        var (root, users) = await FoldersAsync(db);

        // A reference placed without the core's grant, even to a personal space, is refused.
        db.DocumentReferences.Add(new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentFolderId = users.Id,
            TargetDocumentId = spaceId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Removing the core's reference by hand is refused too.
        var placed = await db.DocumentReferences.SingleAsync(r => r.ParentFolderId == users.Id);
        db.DocumentReferences.Remove(placed);
        await Assert.ThrowsAsync<CoreOwnedFolderProtectedException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // A plain folder under Users or under the root is refused by containment, as any exclusive folder refuses.
        var folderMask = await MaskVersionAsync(db, WellKnownMaskIds.Folder);
        db.Documents.Add(new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = users.Id, Name = "Notes", MaskVersionId = folderMask, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Documents.Add(new Document { Id = Guid.NewGuid(), TenantId = _tenantId, ParentId = root.Id, Name = "Notes", MaskVersionId = folderMask, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // A second Administration root, even with the grant: there is only ever one.
        var second = Root("Administration (2)", userId, await MaskVersionAsync(db, WellKnownMaskIds.Administration));
        db.PermitCoreOwnedMask(second.Id);
        db.Documents.Add(second);
        await Assert.ThrowsAsync<CoreOwnedMaskException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Without_a_standard_repository_nothing_is_created_and_a_taken_name_is_stepped_over()
    {
        var (connection, accessor, userId, _) = await TenantAsync();
        using var _c = connection;
        using var db = Ctx(connection, accessor);

        var tenant = await db.Tenants.SingleAsync();
        var standardId = tenant.StandardRepositoryId;
        tenant.StandardRepositoryId = null;
        await db.SaveChangesAsync();
        await EnsureAsync(db);
        Assert.Null(await AdministrationFolders.FindAsync(db, _tenantId, WellKnownMaskIds.Administration, CancellationToken.None));

        tenant.StandardRepositoryId = standardId;
        db.Documents.Add(Root(AdministrationFolders.RootName, userId, await MaskVersionAsync(db, WellKnownMaskIds.Repository)));
        await db.SaveChangesAsync();
        await EnsureAsync(db);
        var (root, _) = await FoldersAsync(db);
        Assert.Equal("Administration (2)", root.Name);
    }
}
