using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Alcifra.EndToEndTests;

// ADR 0509: the WebDAV gateway exposes a SINGLE resource, served at /Alcifra (so an OS mount is named
// "Alcifra"), whose top-level structure is exactly the user's Repositories tree-pane — the "Personal" space
// plus the shared repositories they can see. The legacy /webdav path stays a working alias (hrefs canonicalised
// to /Alcifra), and a plain browser GET of /webdav 301-redirects to /Alcifra. These guard the
// principle + backward compatibility against drift.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public partial class WebDavTreeParityTests
{
    // The personal space is named after its owner (ADR 0671), so its WebDAV/IMAP path segment is
    // whatever this test seeded as the display name — not the constant "Personal" it used to be.
    private const string Personal = "Dav User";

    private readonly E2EApiFactory _factory;

    public WebDavTreeParityTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Webdav_is_the_single_mount_and_mirrors_the_repositories_tree()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        await TestJson.Post(owner, "/api/repositories", new { name = $"Alpha{Guid.NewGuid():N}"[..12] });
        await TestJson.Post(owner, "/api/repositories", new { name = $"Beta{Guid.NewGuid():N}"[..12] });

        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        // The advertised mount URL is the single /webdav resource.
        Assert.EndsWith(("/" + global::Alcifra.Presentation.WireNames.WebDavSegment), (await TestJson.Get(api, "/api/me/devices")).GetProperty("servers").GetProperty("webDavUrl").GetString());

        // The Repositories tree-pane's top level = the "Personal" node + the repositories the user can see.
        var repos = (await TestJson.Get(api, "/api/repositories")).GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()!).ToHashSet();
        var expectedTopLevel = new HashSet<string>(repos) { Personal };

        // PROPFIND the single /webdav resource: root named "webdav", children == the tree top level.
        var names = await PropFindNamesAsync(dav, basic, ("/" + global::Alcifra.Presentation.WireNames.WebDavSegment));
        Assert.Contains(global::Alcifra.Presentation.WireNames.WebDavSegment, names);
        Assert.Equal(expectedTopLevel, names.Where(n => n != global::Alcifra.Presentation.WireNames.WebDavSegment).ToHashSet());

        // One mount, one path (#794, ADR 0987): the earlier mount paths answer like any unknown route, so a mount saved
        // under them is added again rather than served by a second way in.
        using var legacy = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var earlier in new[] { $"/{(global::Alcifra.Localization.PersistedNames.EarlierProduct)}", "/Alcifra" })
        {
            using var legacyProbe = new HttpRequestMessage(new HttpMethod("PROPFIND"), earlier) { Headers = { Authorization = basic } };
            legacyProbe.Headers.TryAddWithoutValidation("Depth", "1");
            Assert.NotEqual(HttpStatusCode.MultiStatus, (await legacy.SendAsync(legacyProbe)).StatusCode);
        }
    }

    private static async Task<List<string>> PropFindNamesAsync(HttpClient dav, AuthenticationHeaderValue basic, string path)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = basic } };
        req.Headers.TryAddWithoutValidation("Depth", "1");
        var response = await dav.SendAsync(req);
        var xml = await response.Content.ReadAsStringAsync();
        return DisplayNameRegex().Matches(xml).Select(m => m.Groups[1].Value).ToList();
    }

    [GeneratedRegex("<D:displayname>([^<]*)</D:displayname>")]
    private static partial Regex DisplayNameRegex();
}
