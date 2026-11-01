using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Alcifra.EndToEndTests;

// #1970 on a real Postgres: today's two protocol passwords become legacy devices in the ProtocolDevices migration,
// with the same hash and the protocols each reached before, so no device breaks on upgrade (owner, 2026-10-29).
// Self-contained (its own throwaway Postgres container), like MigrationRoundTripTests. Needs Docker.
[Trait("Area", "e2e-1")]
public class ProtocolDeviceMigrationTests : IAsyncLifetime
{
    // The migration just before ProtocolDevices: Users already has both hashes, ProtocolDevices does not exist yet.
    private const string BeforeDevices = "20261031152820_IdentityDomainsAndAliases";

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
    public async Task Both_earlier_passwords_become_legacy_devices_with_the_same_hash()
    {
        var tenantId = Guid.NewGuid();
        var both = Guid.NewGuid();
        var davOnly = Guid.NewGuid();
        var neither = Guid.NewGuid();

        await using (var context = Context())
        {
            await context.GetService<IMigrator>().MigrateAsync(BeforeDevices);
        }

        await using (var context = Context(tenantId))
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Name = "Upgraded", CreatedAt = DateTimeOffset.UtcNow });
            context.Users.Add(new User
            {
                Id = both,
                TenantId = tenantId,
                Email = "both@example.com",
                DisplayName = "Both",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            context.Users.Add(new User
            {
                Id = davOnly,
                TenantId = tenantId,
                Email = "dav@example.com",
                DisplayName = "Dav",
                CreatedAt = DateTimeOffset.UtcNow,
                IsActive = false,
            });
            context.Users.Add(new User
            {
                Id = neither,
                TenantId = tenantId,
                Email = "none@example.com",
                DisplayName = "None",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();

            // The hash columns are no longer in the model (#2059), but they exist at this schema state: set them in SQL.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Users\" SET \"WebDavPasswordHash\" = 'dav-hash', \"ImapPasswordHash\" = 'imap-hash' WHERE \"Id\" = {both}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Users\" SET \"WebDavPasswordHash\" = 'dav-only-hash' WHERE \"Id\" = {davOnly}");
        }

        await using (var context = Context())
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = Context(tenantId))
        {
            var devices = await context.ProtocolDevices.AsNoTracking().ToListAsync();

            var dav = Assert.Single(devices, d => d.UserId == both && d.Kind == ProtocolDeviceKind.LegacyDav);
            Assert.Equal(("Earlier WebDAV/calendar password", "dav-hash", tenantId), (dav.Name, dav.SecretHash, dav.TenantId));
            Assert.Equal((false, true, true, true), (dav.MailEnabled, dav.FilesEnabled, dav.CalendarsEnabled, dav.ContactsEnabled));
            Assert.Null(dav.CredentialKey);

            var imap = Assert.Single(devices, d => d.UserId == both && d.Kind == ProtocolDeviceKind.LegacyImap);
            Assert.Equal(("Earlier IMAP password", "imap-hash"), (imap.Name, imap.SecretHash));
            Assert.Equal((true, false, false, false), (imap.MailEnabled, imap.FilesEnabled, imap.CalendarsEnabled, imap.ContactsEnabled));

            // An inactive user's password is kept too, so reactivating the account keeps the device.
            Assert.Single(devices, d => d.UserId == davOnly && d.Kind == ProtocolDeviceKind.LegacyDav && d.SecretHash == "dav-only-hash");
            Assert.DoesNotContain(devices, d => d.UserId == neither);
            Assert.Equal(3, devices.Count);
            Assert.All(devices, d => Assert.NotEqual(Guid.Empty, d.ConcurrencyToken));
        }
    }
}
