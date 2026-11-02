using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Api.ProtocolDevices;
using Alcifra.Api.Provisioning;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// #1970: the demo logins' earlier passwords are devices made by the demo seed, and the published demo password must open
// WebDAV, calendars and mail with them, as it did before devices.
public class DemoProtocolDeviceTests
{
    private const string DemoPassword = "SimplDemo2026!";

    [Fact]
    public async Task The_published_demo_password_opens_every_protocol_through_the_one_seeded_device()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        var user = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "anna@alcifra.local", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow };

        await using (var seed = new AlcifraDbContext(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options,
                         new CurrentTenantAccessor { TenantId = tenantId }))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Demo", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            await DemoSeedItems.EnableDavAndImapAsync(seed, new PasswordHasher<User>(), DemoPassword, [user.Id], DateTimeOffset.UtcNow);
        }

        // The login, as WebDAV and IMAP make it: before any tenant is known.
        await using var login = new AlcifraDbContext(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor());
        // ONE device with the demo password for every protocol (ADR 1030), not the two earlier passwords.
        var device = Assert.Single(await login.ProtocolDevices.IgnoreQueryFilters(["TenantFilter"]).ToListAsync());
        Assert.Equal(ProtocolDeviceKind.Seeded, device.Kind);
        Assert.Equal($"Demo device (password {DemoPassword})", device.Name);

        var authenticator = new ProtocolDeviceAuthenticator(login, TimeProvider.System, NullLogger<ProtocolDeviceAuthenticator>.Instance);
        foreach (var protocol in Enum.GetValues<DeviceProtocol>())
        {
            Assert.NotNull(await authenticator.SignInAsync("anna@alcifra.local", DemoPassword, protocol));
        }
    }

    // A demo login that still has the two earlier passwords from before (a database seeded by an earlier release) loses
    // them when the seed runs again, and gets the one device.
    [Fact]
    public async Task The_seed_replaces_a_demo_logins_earlier_passwords_with_the_one_device()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        var user = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "tom@alcifra.local", DisplayName = "Tom", CreatedAt = DateTimeOffset.UtcNow };
        await using var seed = new AlcifraDbContext(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });
        await seed.Database.EnsureCreatedAsync();
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Demo", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(user);
        var hash = new PasswordHasher<User>().HashPassword(user, DemoPassword);
        seed.ProtocolDevices.Add(LegacyProtocolDevices.New(user, ProtocolDeviceKind.LegacyDav, hash, DateTimeOffset.UtcNow));
        seed.ProtocolDevices.Add(LegacyProtocolDevices.New(user, ProtocolDeviceKind.LegacyImap, hash, DateTimeOffset.UtcNow));
        await seed.SaveChangesAsync();

        await DemoSeedItems.EnableDavAndImapAsync(seed, new PasswordHasher<User>(), DemoPassword, [user.Id], DateTimeOffset.UtcNow);
        await DemoSeedItems.EnableDavAndImapAsync(seed, new PasswordHasher<User>(), DemoPassword, [user.Id], DateTimeOffset.UtcNow);   // idempotent

        Assert.Equal(ProtocolDeviceKind.Seeded, Assert.Single(await seed.ProtocolDevices.ToListAsync()).Kind);
    }
}
