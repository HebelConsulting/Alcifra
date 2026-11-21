using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alcifra.EndToEndTests;

/// <summary>
/// A context for the MIGRATION tests' older schemas: today's model minus the columns that arrived AFTER the revision
/// under test, so a fixture row can be written at that revision without the INSERT naming a column the database does
/// not have yet.
/// </summary>
/// <remarks>
/// The migration tests deliberately stop at an older migration, then seed the rows the migration acts on. Seeding
/// through today's model means EF writes every mapped column — including ones added later — and the insert failed
/// with <c>42703</c> the moment the runtime-brand columns landed (ADR 1103). The ignore list below is the mirror of
/// the raw-SQL workaround those same tests already use for columns REMOVED from the model: one direction is SQL, the
/// other is this. When a later additive column breaks the same insert, add it here (or write that row in SQL).
/// </remarks>
internal sealed class LegacySchemaContext(
    DbContextOptions<AlcifraDbContext> options, ICurrentTenantAccessor currentTenantAccessor)
    : AlcifraDbContext(options, currentTenantAccessor)
{
    /// <summary>
    /// The context for WRITING a fixture row at the older schema. Run the MIGRATIONS through the ordinary
    /// <see cref="AlcifraDbContext"/>: EF looks for migrations in the context type's own assembly, and this type's is
    /// the test project, so <c>MigrateAsync("…")</c> here would answer "the migration was not found".
    /// </summary>
    public static LegacySchemaContext For(string connectionString, Guid? tenantId = null) =>
        new(
            new DbContextOptionsBuilder<AlcifraDbContext>().UseNpgsql(connectionString).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Runtime brand (ADR 1103): Tenants.BrandPackId/BrandPalette/AllowUserPaletteOverride and Users.BrandPalette.
        modelBuilder.Entity<Tenant>()
            .Ignore(t => t.BrandPackId)
            .Ignore(t => t.BrandPalette)
            .Ignore(t => t.AllowUserPaletteOverride);
        modelBuilder.Entity<User>().Ignore(u => u.BrandPalette);
    }
}
