using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

public class StatusDeactivatedAtConsistencyTests
{
    private static AlcifraDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AlcifraDbContext>()
            .UseSqlite(connection)
            .Options;

        return new AlcifraDbContext(options, new CurrentTenantAccessor());
    }

    [Fact]
    public async Task Rejects_an_active_tenant_with_a_DeactivatedAt_value()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Tenant",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            DeactivatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Rejects_a_deactivated_tenant_with_no_DeactivatedAt_value()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Tenant",
            Status = TenantStatus.Deactivated,
            CreatedAt = DateTimeOffset.UtcNow,
            DeactivatedAt = null,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_consistent_active_and_deactivated_states()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        using (var setupContext = CreateContext(connection))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using var context = CreateContext(connection);
        context.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Active Tenant", Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow, DeactivatedAt = null });

        var affected = await context.SaveChangesAsync();

        Assert.Equal(1, affected);
    }
}
