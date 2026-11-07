using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using Alcifra.ModuleAbi;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcifra.IntegrationTests;

// A module's seed names what a reference field accepts (ABI 2.1, ADR 1068), and every heal reconciles the rows to it: a
// sibling mask declared later in the list is found, a mask dropped stops being accepted, and a declaration that cannot hold
// is skipped rather than failing the activation.
public class ModuleFieldAcceptedMasksTests
{
    private static readonly Guid VideoMask = Guid.Parse("0a560000-0000-0000-0000-000000000001");
    private static readonly Guid ThumbnailMask = Guid.Parse("0a560000-0000-0000-0000-000000000002");
    private static readonly Guid PosterMask = Guid.Parse("0a560000-0000-0000-0000-000000000003");
    private static readonly Guid NoSuchMask = Guid.Parse("0a560000-0000-0000-0000-000000000004");

    private readonly Guid _tenant = Guid.NewGuid();

    private sealed class VideoModule(IReadOnlyList<Guid>? accepted, IReadOnlyList<Guid>? onText = null) : IIndustryModule
    {
        public string ModuleId => "videos";
        public string DisplayName => "Videos";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;

        // The field's masks come AFTER it in the list: the rows wait for the second pass.
        public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
        [
            new(VideoMask, "Video", IsFolderMask: false, IsBookable: false, Fields:
            [
                new ModuleFieldSeed("Thumbnail", "DocumentReference") { AcceptedMasks = accepted },
                new ModuleFieldSeed("See also", "DocumentReference"),
                new ModuleFieldSeed("Note", "Text") { AcceptedMasks = onText },
            ]),
            new(ThumbnailMask, "Thumbnail", IsFolderMask: false, IsBookable: false, Fields: []),
            new(PosterMask, "Poster", IsFolderMask: false, IsBookable: false, Fields: []),
        ];

        public void ConfigureServices(IServiceCollection services) { }
    }

    private AlcifraDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor { TenantId = _tenant });

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = _tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenant);
        return connection;
    }

    private async Task SeedAsync(SqliteConnection connection, IIndustryModule module)
    {
        using var db = Context(connection);
        await new ModuleMaskSeeder(db, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, _tenant);
    }

    private async Task<List<Guid>> AcceptedByAsync(SqliteConnection connection, string fieldName)
    {
        using var db = Context(connection);
        return await db.FieldDefinitionAcceptedMasks
            .Where(a => db.FieldDefinitions.Any(f => f.Id == a.FieldDefinitionId && f.Name == fieldName))
            .Select(a => a.MaskId)
            .OrderBy(id => id)
            .ToListAsync();
    }

    [Fact]
    public async Task A_field_names_a_sibling_declared_after_it_and_a_field_naming_none_stays_open()
    {
        await using var connection = await OpenAsync();

        await SeedAsync(connection, new VideoModule([ThumbnailMask]));

        Assert.Equal([ThumbnailMask], await AcceptedByAsync(connection, "Thumbnail"));
        Assert.Empty(await AcceptedByAsync(connection, "See also"));
    }

    [Fact]
    public async Task A_heal_adds_what_the_seed_now_names_and_removes_what_it_dropped()
    {
        await using var connection = await OpenAsync();
        await SeedAsync(connection, new VideoModule([ThumbnailMask]));

        await SeedAsync(connection, new VideoModule([PosterMask]));

        Assert.Equal([PosterMask], await AcceptedByAsync(connection, "Thumbnail"));
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing()
    {
        await using var connection = await OpenAsync();

        await SeedAsync(connection, new VideoModule([ThumbnailMask, PosterMask]));
        await SeedAsync(connection, new VideoModule([ThumbnailMask, PosterMask]));

        Assert.Equal(2, (await AcceptedByAsync(connection, "Thumbnail")).Count);
    }

    [Fact]
    public async Task A_seed_that_stops_naming_masks_opens_the_field_again()
    {
        await using var connection = await OpenAsync();
        await SeedAsync(connection, new VideoModule([ThumbnailMask]));

        await SeedAsync(connection, new VideoModule(null));

        Assert.Empty(await AcceptedByAsync(connection, "Thumbnail"));
    }

    [Fact]
    public async Task A_mask_the_tenant_lacks_and_a_field_of_another_type_are_skipped_not_fatal()
    {
        await using var connection = await OpenAsync();

        await SeedAsync(connection, new VideoModule([NoSuchMask, ThumbnailMask], onText: [ThumbnailMask]));

        Assert.Equal([ThumbnailMask], await AcceptedByAsync(connection, "Thumbnail"));
        Assert.Empty(await AcceptedByAsync(connection, "Note"));
    }
}
