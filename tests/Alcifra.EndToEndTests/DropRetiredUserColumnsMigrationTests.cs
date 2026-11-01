using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Alcifra.Domain.Notifications;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Alcifra.EndToEndTests;

// #2059 on a real Postgres (ADR 1027): dropping the retired Users columns REFUSES while an active user holds a
// self-service certificate that v0.56.0's startup never carried, as after a jump from v0.55.0 straight to v0.57.0, and
// goes ahead once the carry has accounted for it. Self-contained (its own throwaway Postgres container), like
// ProtocolDeviceMigrationTests. Needs Docker.
[Trait("Area", "e2e-1")]
public class DropRetiredUserColumnsMigrationTests : IAsyncLifetime
{
    // The migration just before the drop: Users still has the three columns.
    private const string BeforeDrop = "20261031230012_ProtocolDeviceHandOvers";

    private PostgreSqlContainer _postgres = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("simplarchive")
            .Build();
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private AlcifraDbContext Context(Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    [Fact]
    public async Task An_uncarried_certificate_stops_the_drop_until_the_carry_has_accounted_for_it()
    {
        var tenantId = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var inactive = Guid.NewGuid();

        await using (var context = Context())
        {
            await context.GetService<IMigrator>().MigrateAsync(BeforeDrop);
        }

        await using (var context = Context(tenantId))
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Name = "Skipped", CreatedAt = DateTimeOffset.UtcNow });
            context.Users.Add(new User
            {
                Id = holder,
                TenantId = tenantId,
                Email = "holder@example.com",
                DisplayName = "Holder",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            context.Users.Add(new User
            {
                Id = inactive,
                TenantId = tenantId,
                Email = "gone@example.com",
                DisplayName = "Gone",
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = false,
            });
            await context.SaveChangesAsync();

            // The column is no longer in the model, but it exists at this schema state: set it in SQL. An inactive user's
            // certificate never stops the drop (the carry skipped them too).
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Users\" SET \"SmimeCertificatePem\" = '-----BEGIN CERTIFICATE-----' WHERE \"Id\" IN ({holder}, {inactive})");
        }

        await using (var context = Context())
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());
            Assert.Contains("1 active user(s) hold a self-service certificate", refused.MessageText);
            Assert.Contains("Run v0.56.0 first", refused.MessageText);
        }

        await using (var context = Context(tenantId))
        {
            // What v0.56.0's carry leaves behind for a certificate it could not carry: the retirement notification.
            context.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                RecipientUserId = holder,
                Type = NotificationType.OwnCertificateRetired,
                Title = "Your encrypted-mail certificate was retired",
                Body = "Enrol a hardware key.",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = Context())
        {
            await context.Database.MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            var remaining = await context.Database.SqlQueryRaw<string>(
                    """SELECT column_name AS "Value" FROM information_schema.columns WHERE table_name = 'Users' AND column_name IN ('WebDavPasswordHash', 'ImapPasswordHash', 'SmimeCertificatePem')""")
                .ToListAsync();
            Assert.Empty(remaining);
        }
    }
}
