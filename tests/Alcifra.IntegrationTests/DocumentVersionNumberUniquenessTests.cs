using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// The uniqueness guard behind ADR 1113: two versions of ONE document may not share a number — not even the number 1 —
// while any number of Pending versions (no number yet) coexist. The model half of the test catches a schema edit that
// silently drops IsUnique; the data half proves the index actually refuses, on the tests' own provider.
public class DocumentVersionNumberUniquenessTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_pair_index_is_unique_in_the_model()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        using var db = CreateContext(connection, new CurrentTenantAccessor());

        var index = db.Model.FindEntityType(typeof(DocumentVersion))!.GetIndexes()
            .Single(candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual(["DocumentId", "VersionNumber"]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public async Task Two_confirmed_versions_cannot_share_a_number()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, accessor))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var (tenantId, documentId, userId) = await SeedTenantAsync(connection, accessor);
        accessor.TenantId = tenantId;

        using var db = CreateContext(connection, accessor);
        db.DocumentVersions.Add(Confirmed(tenantId, documentId, userId, number: 1, hash: new string('a', 64)));
        await db.SaveChangesAsync();

        db.DocumentVersions.Add(Confirmed(tenantId, documentId, userId, number: 1, hash: new string('b', 64)));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Pending_versions_without_a_number_coexist()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var accessor = new CurrentTenantAccessor();
        using (var setup = CreateContext(connection, accessor))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var (tenantId, documentId, userId) = await SeedTenantAsync(connection, accessor);
        accessor.TenantId = tenantId;

        using var db = CreateContext(connection, accessor);
        db.DocumentVersions.Add(Pending(tenantId, documentId, userId));
        db.DocumentVersions.Add(Pending(tenantId, documentId, userId));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.DocumentVersions.CountAsync(v => v.DocumentId == documentId));
    }

    private static AlcifraDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor accessor) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, accessor);

    private static async Task<(Guid TenantId, Guid DocumentId, Guid UserId)> SeedTenantAsync(
        SqliteConnection connection, CurrentTenantAccessor accessor)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        using var seed = CreateContext(connection, accessor);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = $"Acme {tenantId:N}", CreatedAt = At });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "owner@acme.test", DisplayName = "Owner", CreatedAt = At });
        seed.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Versions", CreatedAt = At, CreatedByUserId = userId });
        await seed.SaveChangesAsync();
        return (tenantId, documentId, userId);
    }

    private static DocumentVersion Confirmed(Guid tenantId, Guid documentId, Guid userId, int number, string hash) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = number,
            Sha256Hash = hash,
            ObjectKey = $"tenants/{tenantId:N}/2026/{Guid.NewGuid():N}/content.pdf",
            CreatedByUserId = userId,
            CreatedAt = At,
            SizeBytes = 1,
        };

    private static DocumentVersion Pending(Guid tenantId, Guid documentId, Guid userId) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            VersionNumber = null,
            ObjectKey = $"tenants/{tenantId:N}/2026/{Guid.NewGuid():N}/content.pdf",
            CreatedByUserId = userId,
            CreatedAt = At,
        };
}
