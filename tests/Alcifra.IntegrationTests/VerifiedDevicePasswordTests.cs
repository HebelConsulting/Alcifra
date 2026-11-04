using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Api.ProtocolDevices;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

// Remembering a verified password saves the hash and nothing else (#2161, ADR 1050): whatever would refuse a sign-in still
// refuses the next request.
public sealed class VerifiedDevicePasswordTests : IAsyncLifetime
{
    private const string Password = "a-long-device-password";

    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly VerifiedDevicePasswords _memory = new();
    private readonly Clock _clock = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _deviceId = Guid.NewGuid();
    private User _user = null!;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private AlcifraDbContext Context(Guid? tenant = null) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(_connection).Options, new CurrentTenantAccessor { TenantId = tenant });

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _user = new User { Id = Guid.NewGuid(), TenantId = _tenant, Email = "dev@example.test", DisplayName = "Dev", CreatedAt = DateTimeOffset.UtcNow };
        await using var seed = Context(_tenant);
        await seed.Database.EnsureCreatedAsync();
        seed.Tenants.Add(new Tenant { Id = _tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(_user);
        seed.ProtocolDevices.Add(new ProtocolDevice
        {
            Id = _deviceId,
            TenantId = _tenant,
            UserId = _user.Id,
            Name = "Mount",
            Kind = ProtocolDeviceKind.LegacyDav,
            FilesEnabled = true,
            SecretHash = new PasswordHasher<User>().HashPassword(_user, Password),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await seed.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<DeviceSignIn?> SignInAsync(string password = Password)
    {
        await using var context = Context();
        return await new ProtocolDeviceAuthenticator(context, _clock, NullLogger<ProtocolDeviceAuthenticator>.Instance, _memory)
            .SignInAsync(_user.Email, password, DeviceProtocol.Files);
    }

    private Task ChangeAsync(Action<ProtocolDevice> change) => ChangeDeviceAsync(change);

    private async Task ChangeDeviceAsync(Action<ProtocolDevice> change)
    {
        await using var context = Context(_tenant);
        change(await context.ProtocolDevices.SingleAsync(d => d.Id == _deviceId));
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_verified_password_is_remembered_and_a_wrong_one_is_not()
    {
        Assert.Null(_memory.Find(_user.Id, Password, DeviceProtocol.Files, _clock.Now));
        Assert.NotNull(await SignInAsync());
        Assert.Equal(_deviceId, _memory.Find(_user.Id, Password, DeviceProtocol.Files, _clock.Now)?.DeviceId);
        Assert.NotNull(await SignInAsync());

        Assert.Null(await SignInAsync("another password"));
        Assert.Null(_memory.Find(_user.Id, "another password", DeviceProtocol.Files, _clock.Now));
    }

    [Fact]
    public async Task It_is_forgotten_after_the_lifetime_and_use_does_not_extend_it()
    {
        Assert.NotNull(await SignInAsync());
        _clock.Now += VerifiedDevicePasswords.Lifetime / 2;
        Assert.NotNull(await SignInAsync());

        _clock.Now += VerifiedDevicePasswords.Lifetime / 2;
        Assert.Null(_memory.Find(_user.Id, Password, DeviceProtocol.Files, _clock.Now));
    }

    [Fact]
    public async Task A_revoked_device_is_refused_at_once()
    {
        Assert.NotNull(await SignInAsync());
        await using (var context = Context(_tenant))
        {
            context.ProtocolDevices.Remove(await context.ProtocolDevices.SingleAsync(d => d.Id == _deviceId));
            await context.SaveChangesAsync();
        }

        Assert.Null(await SignInAsync());
    }

    [Fact]
    public async Task A_switch_turned_off_is_refused_at_once()
    {
        Assert.NotNull(await SignInAsync());
        await ChangeAsync(d => d.FilesEnabled = false);

        Assert.Null(await SignInAsync());
    }

    [Fact]
    public async Task A_changed_password_is_refused_at_once()
    {
        Assert.NotNull(await SignInAsync());
        await ChangeAsync(d => d.SecretHash = new PasswordHasher<User>().HashPassword(_user, "the new password"));

        Assert.Null(await SignInAsync());
        Assert.NotNull(await SignInAsync("the new password"));
    }

    [Fact]
    public async Task A_deactivated_user_is_refused_at_once()
    {
        Assert.NotNull(await SignInAsync());
        await using (var context = Context(_tenant))
        {
            (await context.Users.SingleAsync(u => u.Id == _user.Id)).IsActive = false;
            await context.SaveChangesAsync();
        }

        Assert.Null(await SignInAsync());
    }
}
