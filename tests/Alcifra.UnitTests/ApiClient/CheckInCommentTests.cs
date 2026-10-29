using System.Text.Json;
using Alcifra.ApiClient;

namespace Alcifra.UnitTests.ApiClient;

// A check-in carries the user's optional comment on the advertised `checkin` rel (ADR 0952), and the two-argument
// form every existing caller compiles against still checks in — with no comment.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class CheckInCommentTests : ApiClientSettingsTest
{
    private static LoopbackServer Server() => new((method, path, _) => (method, path) switch
    {
        ("GET", "/api") => (200, """{"links":[{"rel":"checkouts","href":"/api/checkouts"}]}"""),
        ("GET", "/api/checkouts") => (200, """{"items":[{"id":"6f1b0c4e-3d9a-4c55-9f0e-1a2b3c4d5e6f","name":"Contract","isModified":true,"links":[{"rel":"checkin","href":"/api/documents/6f1b0c4e-3d9a-4c55-9f0e-1a2b3c4d5e6f/checkout/checkin","method":"POST"}]}]}"""),
        ("POST", "/api/documents/6f1b0c4e-3d9a-4c55-9f0e-1a2b3c4d5e6f/checkout/checkin") => (204, string.Empty),
        _ => (404, "{}"),
    });

    [Fact]
    public async Task A_check_in_with_a_comment_posts_it_on_the_checkin_rel()
    {
        using var server = Server();
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');
        var client = new CheckoutClient(new ApiCore("access-1"));

        await client.CheckInFromStashAsync((await client.GetCheckoutsAsync()).Single(), "Fixed the totals");

        var post = server.Requests.Single(r => r.Method == "POST");
        Assert.Equal("Fixed the totals", JsonDocument.Parse(post.Body).RootElement.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task The_two_argument_check_in_sends_no_comment()
    {
        using var server = Server();
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');
        var client = new CheckoutClient(new ApiCore("access-1"));

        await client.CheckInFromStashAsync((await client.GetCheckoutsAsync()).Single());

        var post = server.Requests.Single(r => r.Method == "POST");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(post.Body).RootElement.GetProperty("comment").ValueKind);
    }
}
