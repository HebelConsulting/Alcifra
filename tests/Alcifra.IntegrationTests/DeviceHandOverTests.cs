using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.ProtocolDevices;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// #1970 slice 2: the one-time profile link (owner, 2026-10-29/31). Taken ONCE, by the delete that removes it; gone
// after its lifetime; the stored row holds the profile sealed and only the token's hash.
public class DeviceHandOverTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string Profile = "<plist>the profile, password included</plist>";

    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();

    private static AlcifraDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private async Task<(SqliteConnection Connection, ProtocolDevice Device)> SeedAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        var user = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = "a@example.test", DisplayName = "A", CreatedAt = _clock.Now };
        var device = new ProtocolDevice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = user.Id,
            Name = "Phone",
            CredentialKey = "abcdefgh",
            SecretHash = "h",
            Kind = ProtocolDeviceKind.Enrolled,
            CreatedAt = _clock.Now,
        };
        await using var context = Context(connection, tenantId);
        await context.Database.EnsureCreatedAsync();
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = _clock.Now });
        context.Users.Add(user);
        context.ProtocolDevices.Add(device);
        await context.SaveChangesAsync();
        return (connection, device);
    }

    [Fact]
    public async Task A_link_gives_the_profile_once_and_then_nothing()
    {
        var (connection, device) = await SeedAsync();
        await using var open = connection;

        string token;
        await using (var context = Context(connection, device.TenantId))
        {
            (token, _) = new DeviceHandOvers(context, _keys, _clock).Create(device, Profile);
            await context.SaveChangesAsync();

            var stored = await context.ProtocolDeviceHandOvers.SingleAsync();
            Assert.DoesNotContain(token, stored.TokenHash);
            Assert.DoesNotContain("password included", System.Text.Encoding.UTF8.GetString(stored.SealedProfile));
        }

        // The anonymous request knows no tenant.
        await using (var context = Context(connection))
        {
            var handOvers = new DeviceHandOvers(context, _keys, _clock);
            Assert.True(await handOvers.ExistsAsync(token, CancellationToken.None));

            var taken = await handOvers.TakeAsync(token, CancellationToken.None);
            Assert.Equal(Profile, taken!.Value.Profile);
            Assert.Equal(device.Id, taken.Value.Device.Id);

            Assert.Null(await handOvers.TakeAsync(token, CancellationToken.None));
            Assert.False(await handOvers.ExistsAsync(token, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_link_stops_working_after_its_lifetime_and_the_sweep_removes_it()
    {
        var (connection, device) = await SeedAsync();
        await using var open = connection;

        string token;
        await using (var context = Context(connection, device.TenantId))
        {
            (token, _) = new DeviceHandOvers(context, _keys, _clock).Create(device, Profile);
            await context.SaveChangesAsync();
        }

        _clock.Now += DeviceHandOvers.Lifetime + TimeSpan.FromSeconds(1);
        await using (var context = Context(connection))
        {
            var handOvers = new DeviceHandOvers(context, _keys, _clock);
            Assert.Null(await handOvers.TakeAsync(token, CancellationToken.None));
            Assert.Equal(1, await handOvers.SweepAsync(CancellationToken.None));
            Assert.Empty(await context.ProtocolDeviceHandOvers.IgnoreQueryFilters(["TenantFilter"]).ToListAsync());
        }
    }

    [Fact]
    public async Task Revoking_the_device_withdraws_its_pending_link()
    {
        var (connection, device) = await SeedAsync();
        await using var open = connection;

        string token;
        await using (var context = Context(connection, device.TenantId))
        {
            (token, _) = new DeviceHandOvers(context, _keys, _clock).Create(device, Profile);
            await context.SaveChangesAsync();
            context.ProtocolDevices.Remove(await context.ProtocolDevices.SingleAsync());
            await context.SaveChangesAsync();
        }

        await using (var context = Context(connection))
        {
            Assert.Null(await new DeviceHandOvers(context, _keys, _clock).TakeAsync(token, CancellationToken.None));
        }
    }
}
