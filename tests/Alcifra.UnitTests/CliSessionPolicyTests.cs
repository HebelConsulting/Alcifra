using System.Security.Claims;
using Alcifra.Api.Provisioning;
using OpenIddict.Abstractions;

namespace Alcifra.UnitTests;

// 15 idle minutes, 12 hours at most (ADR 1071). The server's half, since a copied session file is what the limit is for.
public class CliSessionPolicyTests
{
    private static readonly DateTimeOffset Login = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Fresh()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("test"));
        CliSessionPolicy.Begin(principal, Login);
        return principal;
    }

    [Theory]
    [InlineData("alcifra-cli", true)]
    [InlineData("alcifra-pass", true)]
    [InlineData("alcifra-desktop", false)]
    [InlineData(null, false)]
    public void It_governs_the_two_tools_and_no_other_client(string? clientId, bool governed) =>
        Assert.Equal(governed, CliSessionPolicy.Governs(clientId));

    [Fact]
    public void A_login_can_be_renewed_for_15_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Fresh().GetRefreshTokenLifetime());
    }

    [Fact]
    public void A_renewal_inside_the_limit_extends_by_the_idle_limit()
    {
        var principal = Fresh();

        Assert.True(CliSessionPolicy.TryContinue(principal, Login.AddHours(6)));
        Assert.Equal(TimeSpan.FromMinutes(15), principal.GetRefreshTokenLifetime());
    }

    [Fact]
    public void Near_the_limit_no_token_outlives_it()
    {
        var principal = Fresh();

        Assert.True(CliSessionPolicy.TryContinue(principal, Login.AddHours(12).AddMinutes(-10)));
        Assert.Equal(TimeSpan.FromMinutes(10), principal.GetRefreshTokenLifetime());
        Assert.Equal(TimeSpan.FromMinutes(10), principal.GetAccessTokenLifetime());
    }

    [Fact]
    public void At_the_limit_a_renewal_is_refused_however_busy_the_session()
    {
        Assert.False(CliSessionPolicy.TryContinue(Fresh(), Login.AddHours(12)));
    }

    [Fact]
    public void A_token_that_does_not_say_when_it_began_is_refused()
    {
        Assert.False(CliSessionPolicy.TryContinue(new ClaimsPrincipal(new ClaimsIdentity("test")), Login));
    }
}
