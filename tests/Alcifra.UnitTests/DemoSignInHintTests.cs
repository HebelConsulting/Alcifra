using Microsoft.Extensions.Configuration;
using Alcifra.Api.Security;

namespace Alcifra.UnitTests;

// ADR 1075: the sign-in page shows the demo's login only where both its keys are set, the kiosk's two instances alike.
public class DemoSignInHintTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Both_keys_give_the_hint() =>
        Assert.Equal(new DemoSignInHint("admin@demo.example", "pw"), DemoSignInHint.From(Config(("DemoSignIn:Email", "admin@demo.example"), ("DemoSignIn:Password", "pw"))));

    [Theory]
    [InlineData("DemoSignIn:Email")]
    [InlineData("DemoSignIn:Password")]
    public void One_key_alone_gives_none(string key) =>
        Assert.Null(DemoSignInHint.From(Config((key, "x"))));

    // The seeding settings alone never show a login: they sit on one instance, and the page would differ between the two.
    [Fact]
    public void The_seeded_administrator_alone_gives_none() =>
        Assert.Null(DemoSignInHint.From(Config(("Demo:Administrator:Email", "admin@demo.example"), ("Demo:Administrator:Password", "pw"))));
}
