using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

public class TenantNameUniquenessTests
{
    private static AlcifraDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AlcifraDbContext>()
            .UseSqlite(connection)
            .Options;

        return new AlcifraDbContext(options, new CurrentTenantAccessor());
    }

    [Fact]
    public async Task Rejects_two_active_tenants_with_the_same_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Acme Corp", CreatedAt = DateTimeOffset.UtcNow });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Acme Corp", CreatedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_reusing_a_deactivated_tenants_name()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using (var seedContext = CreateContext(connection))
        {
            seedContext.Tenants.Add(new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "Acme Corp",
                Status = TenantStatus.Deactivated,
                CreatedAt = DateTimeOffset.UtcNow,
                DeactivatedAt = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Acme Corp", CreatedAt = DateTimeOffset.UtcNow });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }
}
