using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.ProtocolDevices;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// #1970 slice 2: the profile link (owner, 2026-10-29/31); the stored row holds the profile sealed and only the token's
// hash. ADR 1028 (owner, 2026-11-01): a browser that cannot install a profile hands it to one that can, which fetches the
// link again, so the first take leaves it working for a short grace, MaxTakes times in all.
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
    public async Task A_link_gives_the_profile_at_most_MaxTakes_times_and_then_nothing()
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

            for (var take = 0; take < DeviceHandOvers.MaxTakes; take++)
            {
                var taken = await handOvers.TakeAsync(token, CancellationToken.None);
                Assert.Equal(Profile, taken!.Value.Profile);
                Assert.Equal(device.Id, taken.Value.Device.Id);
            }

            Assert.Null(await handOvers.TakeAsync(token, CancellationToken.None));
            Assert.False(await handOvers.ExistsAsync(token, CancellationToken.None));
            Assert.Empty(await context.ProtocolDeviceHandOvers.IgnoreQueryFilters(["TenantFilter"]).ToListAsync());   // the last take removed it
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
    public async Task The_first_take_leaves_the_link_working_only_for_the_grace()
    {
        var (connection, device) = await SeedAsync();
        await using var open = connection;

        string token;
        await using (var context = Context(connection, device.TenantId))
        {
            (token, _) = new DeviceHandOvers(context, _keys, _clock).Create(device, Profile);
            await context.SaveChangesAsync();
        }

        await using (var context = Context(connection))
        {
            // The first browser downloads it; the installer fetches it again within the grace.
            var handOvers = new DeviceHandOvers(context, _keys, _clock);
            Assert.NotNull(await handOvers.TakeAsync(token, CancellationToken.None));
            _clock.Now += DeviceHandOvers.Grace - TimeSpan.FromSeconds(1);
            Assert.NotNull(await handOvers.TakeAsync(token, CancellationToken.None));

            // The grace counts from the FIRST take, well inside the link's ten minutes.
            _clock.Now += TimeSpan.FromSeconds(2);
            Assert.Null(await handOvers.TakeAsync(token, CancellationToken.None));
            Assert.Equal(1, await handOvers.SweepAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_grace_never_runs_past_the_links_lifetime()
    {
        var (connection, device) = await SeedAsync();
        await using var open = connection;

        string token;
        await using (var context = Context(connection, device.TenantId))
        {
            (token, _) = new DeviceHandOvers(context, _keys, _clock).Create(device, Profile);
            await context.SaveChangesAsync();
        }

        await using (var context = Context(connection))
        {
            var handOvers = new DeviceHandOvers(context, _keys, _clock);
            _clock.Now += DeviceHandOvers.Lifetime - TimeSpan.FromSeconds(30);
            Assert.NotNull(await handOvers.TakeAsync(token, CancellationToken.None));
            _clock.Now += TimeSpan.FromSeconds(31);
            Assert.Null(await handOvers.TakeAsync(token, CancellationToken.None));
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
