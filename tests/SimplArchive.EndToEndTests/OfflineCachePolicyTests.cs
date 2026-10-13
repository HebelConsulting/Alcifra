using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.EndToEndTests;

// What a tenant allows a phone's offline cache (#1742): everything until an administrator narrows it; every user reads
// it (the phone offers only what is allowed), only a tenant administrator writes it, with If-Match (ADR 0878).
// Each test makes its own tenant: it changes the policy (ADR 0884).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class OfflineCachePolicyTests
{
    private readonly E2EApiFactory _factory;

    public OfflineCachePolicyTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync(Guid tenantId, bool admin)
    {
        var email = $"ocp-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "oc-1234", "Policy User", isTenantAdmin: admin);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "oc-1234"));
    }

    // Followed from /me (ADR 0543).
    private static async Task<string> HrefAsync(HttpClient api) =>
        (await TestJson.Get(api, "/api/me")).GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "offlineCachePolicy").GetProperty("href").GetString()!;

    private static object Policy(int minutes = 5, string unlock = "Session", string refresh = "RemoveWhenNewer") => new
    {
        allowed = true,
        notifications = true,
        openedDocuments = false,
        chosenFolders = true,
        unlockMode = unlock,
        refreshMode = refresh,
        maxUnlockedMinutes = minutes,
    };

    private static HttpRequestMessage Put(string href, object body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    [Fact]
    public async Task Every_user_reads_everything_allowed_and_only_an_administrator_may_change_it()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"ocp-{Guid.NewGuid():N}");
        var user = await ClientAsync(tenantId, admin: false);
        var href = await HrefAsync(user);

        using var read = await user.GetAsync(href);
        var policy = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(policy.GetProperty("allowed").GetBoolean() && policy.GetProperty("notifications").GetBoolean()
            && policy.GetProperty("openedDocuments").GetBoolean() && policy.GetProperty("chosenFolders").GetBoolean());
        Assert.Equal(("UserChooses", "UserChooses", 15), (policy.GetProperty("unlockMode").GetString(), policy.GetProperty("refreshMode").GetString(),
            policy.GetProperty("maxUnlockedMinutes").GetInt32()));
        Assert.False(policy.GetProperty("canEdit").GetBoolean());

        using var refused = await user.SendAsync(Put(href, Policy(), read.Headers.ETag?.Tag));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task An_administrator_changes_it_with_the_tag_of_what_they_read_and_bad_values_are_refused()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"ocp-{Guid.NewGuid():N}");
        var admin = await ClientAsync(tenantId, admin: true);
        var href = await HrefAsync(admin);
        using var read = await admin.GetAsync(href);
        var tag = read.Headers.ETag!.Tag;
        Assert.True((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("canEdit").GetBoolean());

        using (var noTag = await admin.SendAsync(Put(href, Policy(), ifMatch: null)))
        {
            Assert.Equal((HttpStatusCode)428, noTag.StatusCode);
        }

        using (var badMinutes = await admin.SendAsync(Put(href, Policy(minutes: 0), tag)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badMinutes.StatusCode);
            Assert.Contains("INVALID_OFFLINE_CACHE_POLICY", await badMinutes.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var badMode = await admin.SendAsync(Put(href, Policy(unlock: "Sometimes"), tag)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
        }

        using var saved = await admin.SendAsync(Put(href, Policy(), tag));
        saved.EnsureSuccessStatusCode();
        var now = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Session", "RemoveWhenNewer", 5, false), (now.GetProperty("unlockMode").GetString(), now.GetProperty("refreshMode").GetString(),
            now.GetProperty("maxUnlockedMinutes").GetInt32(), now.GetProperty("openedDocuments").GetBoolean()));

        using var stale = await admin.SendAsync(Put(href, Policy(minutes: 30), tag));   // the tag of the FIRST read
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Saconsole_changes_only_the_options_given_and_refuses_a_user_who_is_not_an_administrator()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"ocp-{Guid.NewGuid():N}");
        var admin = new SimplArchiveApi(await ClientAsync(tenantId, admin: true));

        var written = await OfflineCacheSurface.SetAsync(admin, new OfflineCacheSetSettings { UnlockMode = "Document", MaxUnlockedMinutes = 3 }, CancellationToken.None);

        Assert.Equal(("Document", "UserChooses", 3, true), (written.GetProperty("unlockMode").GetString(), written.GetProperty("refreshMode").GetString(),
            written.GetProperty("maxUnlockedMinutes").GetInt32(), written.GetProperty("chosenFolders").GetBoolean()));

        var user = new SimplArchiveApi(await ClientAsync(tenantId, admin: false));
        await Assert.ThrowsAsync<CliException>(() => OfflineCacheSurface.SetAsync(user, new OfflineCacheSetSettings { Allowed = false }, CancellationToken.None));
    }
}
