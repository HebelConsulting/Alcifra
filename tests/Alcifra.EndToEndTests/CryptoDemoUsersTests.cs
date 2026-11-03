using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

// The Crypto demo tenant's people (owner, 2026-10-24): crypt founds it, florian, thomas and alex run it with crypt as
// tenant administrators, tanja and mihaela are regular users. Asked of the tenant the factory's CryptoDemo:* seed
// really created, because the kiosk reseeds it from this code every night.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class CryptoDemoUsersTests
{
    private readonly E2EApiFactory _factory;

    public CryptoDemoUsersTests(E2EApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("florian", true)]
    [InlineData("thomas", true)]
    [InlineData("alex", true)]
    [InlineData("tanja", false)]
    [InlineData("mihaela", false)]
    public async Task A_seeded_Crypto_user_is_an_administrator_exactly_when_the_owner_said_so(string localPart, bool administrator)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        var email = $"{localPart}@{E2EApiFactory.CryptoAdminEmail.Split('@')[1]}";
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == E2EApiFactory.CryptoTenantName);

        var user = await db.Users.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == email.ToUpperInvariant());

        Assert.True(user.IsActive);
        Assert.Equal(administrator, user.IsTenantAdmin);
        // An administrator holds every system right, like the founding one (TenantAdministratorRights); a regular
        // user none of them — spot-checked on the two #1241 was about and the one that opens personal spaces.
        Assert.Equal(administrator, user.CanReleaseResources);
        Assert.Equal(administrator, user.CanBlockResources);
        Assert.Equal(administrator, user.CanAccessWithoutGrant);

        // Everyone works in the repository through the same grant, so a demoted administrator keeps it.
        Assert.True(await db.AclEntries.IgnoreQueryFilters(["TenantFilter"])
            .AnyAsync(a => a.DocumentId == tenant.StandardRepositoryId && a.UserId == user.Id && a.CanReadContent && a.CanCreateSubItems));
    }

    // ADR 1039: every seeded Crypto user keeps a confirmed alias on the former login domain, so a reader certificate
    // issued for the former address still names its holder.
    [Fact]
    public async Task Every_seeded_user_has_a_confirmed_alias_on_the_former_domain()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Alcifra.Infrastructure.Persistence.AlcifraDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == E2EApiFactory.CryptoTenantName);
        foreach (var localPart in new[] { "crypt", "florian", "thomas", "alex", "tanja", "mihaela" })
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == $"{localPart}@{E2EApiFactory.CryptoAdminEmail.Split('@')[1]}".ToUpperInvariant());
            var alias = await db.UserIdentityAliases.IgnoreQueryFilters().SingleAsync(a => a.UserId == user.Id && a.NormalizedAddress == $"{localPart}@{E2EApiFactory.CryptoFormerDomain}".ToUpperInvariant());
            Assert.NotNull(alias.ConfirmedAt);
        }
    }

    [Fact]
    public async Task An_account_named_in_the_aliases_setting_gets_that_address_as_a_confirmed_alias()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Alcifra.Infrastructure.Persistence.AlcifraDbContext>();
        var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == E2EApiFactory.CryptoTenantName);
        var florian = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.TenantId == tenant.Id && u.NormalizedEmail == $"florian@{E2EApiFactory.CryptoAdminEmail.Split('@')[1]}".ToUpperInvariant());

        var own = await db.UserIdentityAliases.IgnoreQueryFilters().SingleAsync(a => a.NormalizedAddress == E2EApiFactory.CryptoOwnAddress.ToUpperInvariant());

        Assert.Equal(florian.Id, own.UserId);
        Assert.NotNull(own.ConfirmedAt);
    }
}
