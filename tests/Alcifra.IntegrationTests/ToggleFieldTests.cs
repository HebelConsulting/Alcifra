using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.ModuleAbi;

namespace Alcifra.IntegrationTests;

// The Toggle field (ADR 1047): a module's seed may turn a Text field of a worn mask into one, and the stored values move
// with it, or a published video would read as disabled. Only true and false are stored from then on.
public class ToggleFieldTests
{
    private static readonly Guid MaskId = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001");

    private static ModuleMaskSeed Seed(string dataType) =>
        new(MaskId, "Clip", IsFolderMask: false, IsBookable: false, [new ModuleFieldSeed("Published", dataType)]);

    private sealed class Stub(ModuleMaskSeed seed) : IIndustryModule
    {
        public string ModuleId => "stub";
        public string DisplayName => "Stub";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public bool RequiresLicence => false;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [seed];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
    }

    private static AlcifraDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<(Guid Tenant, Guid Document, Guid Field)> WornMaskAsync(SqliteConnection connection, params string[] values)
    {
        using (var setup = Context(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        using (var seed = Context(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "t@example.com", DisplayName = "T", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using (var context = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new Stub(Seed("Text")), tenantId);
        }

        using var write = Context(connection, tenantId);
        var field = await write.FieldDefinitions.SingleAsync(f => f.Name == "Published");
        for (var i = 0; i < values.Length; i++)
        {
            write.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = field.Id, Value = values[i], Ordinal = i });
        }

        await write.SaveChangesAsync();
        return (tenantId, documentId, field.Id);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task A_seed_turning_a_text_field_into_a_toggle_converts_what_is_stored()
    {
        await using var connection = await OpenAsync();
        var (tenantId, _, fieldId) = await WornMaskAsync(connection, "yes", " ON ", "no", "maybe");

        using (var context = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new Stub(Seed("Toggle")), tenantId);
        }

        using var read = Context(connection, tenantId);
        Assert.Equal(FieldDataType.Toggle, (await read.FieldDefinitions.SingleAsync(f => f.Id == fieldId)).DataType);
        var stored = await read.FieldValues.Where(v => v.FieldDefinitionId == fieldId).OrderBy(v => v.Ordinal).Select(v => v.Value).ToListAsync();
        Assert.Equal(["true", "true", "false", "false"], stored);
    }

    [Fact]
    public async Task A_seed_turning_a_text_field_of_document_ids_into_a_reference_keeps_the_values()
    {
        await using var connection = await OpenAsync();
        var thumbnail = Guid.NewGuid().ToString("D");
        var (tenantId, _, fieldId) = await WornMaskAsync(connection, thumbnail);

        using (var context = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new Stub(Seed("DocumentReference")), tenantId);
        }

        using var read = Context(connection, tenantId);
        Assert.Equal(FieldDataType.DocumentReference, (await read.FieldDefinitions.SingleAsync(f => f.Id == fieldId)).DataType);
        Assert.Equal(thumbnail, (await read.FieldValues.SingleAsync(v => v.FieldDefinitionId == fieldId)).Value);
    }

    [Fact]
    public async Task A_stored_value_that_is_no_document_id_leaves_the_field_text()
    {
        await using var connection = await OpenAsync();
        var (tenantId, _, fieldId) = await WornMaskAsync(connection, Guid.NewGuid().ToString("D"), "not an id");

        using (var context = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new Stub(Seed("DocumentReference")), tenantId);
        }

        using var read = Context(connection, tenantId);
        Assert.Equal(FieldDataType.Text, (await read.FieldDefinitions.SingleAsync(f => f.Id == fieldId)).DataType);
    }

    [Fact]
    public async Task Any_other_seed_leaves_an_existing_field_as_it_is()
    {
        await using var connection = await OpenAsync();
        var (tenantId, _, fieldId) = await WornMaskAsync(connection, "yes");

        using (var context = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new Stub(Seed("Boolean")), tenantId);
        }

        using var read = Context(connection, tenantId);
        Assert.Equal(FieldDataType.Text, (await read.FieldDefinitions.SingleAsync(f => f.Id == fieldId)).DataType);
        Assert.Equal("yes", (await read.FieldValues.SingleAsync(v => v.FieldDefinitionId == fieldId)).Value);
    }

    [Fact]
    public async Task A_toggle_stores_true_or_false_and_refuses_anything_else()
    {
        await using var connection = await OpenAsync();
        var (tenantId, documentId, fieldId) = await WornMaskAsync(connection);
        using (var context = Context(connection, tenantId))
        {
            await new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(new Stub(Seed("Toggle")), tenantId);
        }

        using var write = Context(connection, tenantId);
        write.FieldValues.Add(new FieldValue { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = documentId, FieldDefinitionId = fieldId, Value = "yes" });
        await Assert.ThrowsAsync<FieldValueRejectedException>(() => write.SaveChangesAsync());
    }
}
