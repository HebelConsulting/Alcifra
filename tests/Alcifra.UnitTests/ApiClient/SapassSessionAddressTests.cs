using Alcifra.ApiClient;
using Alcifra.Sapass.Infrastructure;

namespace Alcifra.UnitTests.ApiClient;

// #2042: on a sealed-tier tenant an entry's content address is RELATIVE (/api/encrypted-content?t=…), and the shared
// client resolves it against ApiClientSettings.ApiRootUrl, which each host sets once. apass never set it, so the
// owner's `apass show` signed in to the demo and then fetched the content from the default http://localhost:8080.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class SapassSessionAddressTests : ApiClientSettingsTest
{
    private readonly Dictionary<string, string?> _saved = new[] { Session.UrlVariable, Session.TokenVariable }
        .ToDictionary(name => name, Environment.GetEnvironmentVariable);

    [Fact]
    public void A_relative_content_address_resolves_to_the_installation_apass_signed_in_to()
    {
        ApiClientSettings.ApiRootUrl = "http://localhost:8080";   // the default apass used to leave in place
        Environment.SetEnvironmentVariable(Session.UrlVariable, "https://archive.example/");
        Environment.SetEnvironmentVariable(Session.TokenVariable, "test-token");

        using var session = Session.FromEnvironment();

        Assert.Equal(
            "https://archive.example/api/encrypted-content?t=abc",
            ApiCore.ResolveContentUrl("/api/encrypted-content?t=abc").ToString());
        Assert.True(ApiCore.IsOwnInstallation(new Uri("https://archive.example/api/documents")));
    }

    public override void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        base.Dispose();
    }
}
