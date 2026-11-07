using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// A reference field naming accepted masks refuses a target of any other kind at save (#2214, ADR 1068); a field naming none
// accepts any document, as every reference field did before.
public class ReferenceFieldTargetsTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _videoMask = Guid.NewGuid();
    private readonly Guid _thumbnailMask = Guid.NewGuid();
    private readonly Guid _videoVersion = Guid.NewGuid();
    private readonly Guid _thumbnailVersion = Guid.NewGuid();
    private readonly Guid _restricted = Guid.NewGuid();
    private readonly Guid _open = Guid.NewGuid();
    private readonly Guid _video = Guid.NewGuid();
    private readonly Guid _thumbnail = Guid.NewGuid();

    private AlcifraDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor { TenantId = _tenant });

    private async Task<SqliteConnection> SeedAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new Tenant { Id = _tenant, Name = "Tenant", CreatedAt = now });
        db.Users.Add(new User { Id = _user, TenantId = _tenant, Email = "a@example.com", DisplayName = "A", CreatedAt = now });
        db.Masks.Add(new Mask { Id = _videoMask, TenantId = _tenant, CreatedAt = now });
        db.Masks.Add(new Mask { Id = _thumbnailMask, TenantId = _tenant, CreatedAt = now });
        db.MaskVersions.Add(new MaskVersion { Id = _videoVersion, TenantId = _tenant, MaskId = _videoMask, Name = "Video", CreatedAt = now });
        db.MaskVersions.Add(new MaskVersion { Id = _thumbnailVersion, TenantId = _tenant, MaskId = _thumbnailMask, Name = "Thumbnail", CreatedAt = now });
        db.FieldDefinitions.Add(new FieldDefinition { Id = _restricted, TenantId = _tenant, MaskVersionId = _videoVersion, Name = "Thumbnail", DataType = FieldDataType.DocumentReference, CreatedAt = now });
        db.FieldDefinitions.Add(new FieldDefinition { Id = _open, TenantId = _tenant, MaskVersionId = _videoVersion, Name = "See also", DataType = FieldDataType.DocumentReference, CreatedAt = now });
        db.FieldDefinitionAcceptedMasks.Add(new FieldDefinitionAcceptedMask { Id = Guid.NewGuid(), TenantId = _tenant, FieldDefinitionId = _restricted, MaskId = _thumbnailMask });
        db.Documents.Add(new Document { Id = _video, TenantId = _tenant, Name = "Clip", MaskVersionId = _videoVersion, CreatedByUserId = _user, CreatedAt = now });
        db.Documents.Add(new Document { Id = _thumbnail, TenantId = _tenant, Name = "Still", MaskVersionId = _thumbnailVersion, CreatedByUserId = _user, CreatedAt = now });
        await db.SaveChangesAsync();
        return connection;
    }

    private async Task WriteAsync(SqliteConnection connection, Guid field, Guid target)
    {
        using var db = Context(connection);
        db.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = _tenant, DocumentId = _video, FieldDefinitionId = field, Value = target.ToString() });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_target_of_an_accepted_mask_is_taken()
    {
        using var connection = await SeedAsync();
        await WriteAsync(connection, _restricted, _thumbnail);
    }

    [Fact]
    public async Task A_target_of_another_mask_is_refused_naming_the_field()
    {
        using var connection = await SeedAsync();
        var refused = await Assert.ThrowsAsync<FieldValueRejectedException>(() => WriteAsync(connection, _restricted, _video));
        Assert.Contains("'Thumbnail'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_naming_no_masks_accepts_any_document()
    {
        using var connection = await SeedAsync();
        await WriteAsync(connection, _open, _video);
    }
}
