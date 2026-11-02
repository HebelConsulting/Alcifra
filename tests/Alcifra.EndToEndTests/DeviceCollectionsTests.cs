using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Alcifra.Domain.Masks;

namespace Alcifra.EndToEndTests;

// #2090 (owner decisions 2026-11-02): a device set to CHOSEN collections shows the user's own calendars always and a
// shared one only while it is ticked — in the calendar home's listing and when addressed directly. A device that shows
// all is unchanged; taking an iPhone/iPad profile through its hand-over link switches the device to chosen.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DeviceCollectionsTests
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly E2EApiFactory _factory;

    public DeviceCollectionsTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Api, string Email, Guid SharedId, string SharedName);

    // A user with their own calendar, and a calendar shared with them in a repository they may see.
    private async Task<Rig> SeedAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var email = $"scope-{Guid.NewGuid():N}@e2e.local";
        const string password = "scope-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Scope User");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });   // provisions My Calendar / My Addressbook

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Team {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{userId}", new { canSee = true, canReadContent = true });
        var sharedName = $"Team calendar {Guid.NewGuid():N}"[..22];
        var sharedId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = sharedName, maskId = WellKnownMaskIds.Calendar })).GetProperty("id").GetGuid();
        return new Rig(api, email, sharedId, sharedName);
    }

    private static AuthenticationHeaderValue Basic(string email, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}")));

    private async Task<List<string>> CalendarNamesAsync(AuthenticationHeaderValue auth)
    {
        using var dav = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/") { Headers = { Authorization = auth } };
        request.Headers.TryAddWithoutValidation("Depth", "1");
        var response = await dav.SendAsync(request);
        Assert.Equal(207, (int)response.StatusCode);
        return [.. XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(Dav + "displayname").Select(d => d.Value)];
    }

    private async Task<HttpStatusCode> PropfindCollectionAsync(AuthenticationHeaderValue auth, Guid folderId)
    {
        using var dav = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/caldav/calendars/{folderId}/") { Headers = { Authorization = auth } };
        request.Headers.TryAddWithoutValidation("Depth", "0");
        return (await dav.SendAsync(request)).StatusCode;
    }

    private static string Href(JsonElement resource, string rel) => resource.GetProperty("links").EnumerateArray()
        .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private static async Task<JsonElement> PutDeviceAsync(HttpClient api, JsonElement device, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Href(device, "self")) { Content = JsonContent(body) };
        request.Headers.TryAddWithoutValidation("If-Match", device.GetProperty("eTag").GetString());
        var response = await api.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_chosen_device_shows_its_own_calendars_and_a_shared_one_only_while_ticked()
    {
        var rig = await SeedAsync();
        using var _ = rig.Api;
        var enrolled = await TestJson.Post(rig.Api, "/api/me/devices", new { name = "Phone" });
        var auth = Basic(rig.Email, enrolled.GetProperty("password").GetString()!);
        var device = await TestJson.Get(rig.Api, Href(enrolled, "self"));

        // A device enrolled as before shows everything, the shared calendar included.
        Assert.False(device.GetProperty("collectionsChosen").GetBoolean());
        Assert.Contains(await CalendarNamesAsync(auth), n => n.Contains(rig.SharedName, StringComparison.Ordinal));

        // The collections rel lists the SHARED one to tick, never the user's own.
        var choices = (await TestJson.Get(rig.Api, Href(device, "collections"))).GetProperty("items").EnumerateArray().ToList();
        var shared = Assert.Single(choices, c => c.GetProperty("id").GetGuid() == rig.SharedId);
        Assert.Equal("calendar", shared.GetProperty("kind").GetString());
        Assert.DoesNotContain(choices, c => c.GetProperty("name").GetString()!.EndsWith("My Calendar", StringComparison.Ordinal));

        // Chosen, nothing ticked: own calendars and the task feeds stay, the shared one goes — listed or addressed.
        device = await PutDeviceAsync(rig.Api, device, new { name = "Phone", collectionsChosen = true, chosenCollections = Array.Empty<Guid>() });
        var names = await CalendarNamesAsync(auth);
        Assert.Contains(names, n => n.EndsWith("My Calendar", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains(rig.SharedName, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, await PropfindCollectionAsync(auth, rig.SharedId));

        // Ticked: it is back.
        device = await PutDeviceAsync(rig.Api, device, new { name = "Phone", collectionsChosen = true, chosenCollections = new[] { rig.SharedId } });
        Assert.Equal(rig.SharedId, Assert.Single(device.GetProperty("chosenCollections").EnumerateArray()).GetGuid());
        Assert.Contains(await CalendarNamesAsync(auth), n => n.Contains(rig.SharedName, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindCollectionAsync(auth, rig.SharedId));

        // A save from a client that predates the setting sends neither field and changes neither.
        device = await PutDeviceAsync(rig.Api, device, new { name = "Phone renamed" });
        Assert.True(device.GetProperty("collectionsChosen").GetBoolean());
        Assert.Single(device.GetProperty("chosenCollections").EnumerateArray());
    }

    [Fact]
    public async Task A_tick_that_is_no_shared_collection_is_refused()
    {
        var rig = await SeedAsync();
        using var _ = rig.Api;
        var enrolled = await TestJson.Post(rig.Api, "/api/me/devices", new { name = "Tablet" });
        var device = await TestJson.Get(rig.Api, Href(enrolled, "self"));

        using var request = new HttpRequestMessage(HttpMethod.Put, Href(device, "self"))
        {
            Content = JsonContent(new { name = "Tablet", collectionsChosen = true, chosenCollections = new[] { Guid.NewGuid() } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", device.GetProperty("eTag").GetString());
        var response = await rig.Api.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("PROTOCOL_DEVICE_COLLECTION_UNKNOWN", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Taking_the_iphone_profile_switches_the_device_to_chosen_collections()
    {
        var rig = await SeedAsync();
        using var _ = rig.Api;
        var enrolled = await TestJson.Post(rig.Api, "/api/me/devices", new { name = "iPhone" });
        var link = new Uri(enrolled.GetProperty("handOver").GetProperty("profileUrl").GetString()!).PathAndQuery;

        using var camera = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await camera.GetAsync(link)).StatusCode);

        var device = await TestJson.Get(rig.Api, Href(enrolled, "self"));
        Assert.True(device.GetProperty("collectionsChosen").GetBoolean());
        Assert.NotEqual(enrolled.GetProperty("eTag").GetString(), device.GetProperty("eTag").GetString());   // the editor sees the change
        Assert.DoesNotContain(await CalendarNamesAsync(Basic(rig.Email, enrolled.GetProperty("password").GetString()!)),
            n => n.Contains(rig.SharedName, StringComparison.Ordinal));
    }
}
