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

        var alphaId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Alpha{Guid.NewGuid():N}"[..12] })).GetProperty("id").GetGuid();
        await TestJson.Post(owner, "/api/repositories", new { name = $"Beta{Guid.NewGuid():N}"[..12] });

        // The Administration folder (ADR 1066), so the one named exception below is exercised rather than absent.
        await _factory.EnsureAdministrationFoldersAsync(tenantId, alphaId);

        var email = $"dav-{Guid.NewGuid():N}@e2e.local";
        const string password = "dav-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Dav User");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
        using var dav = _factory.CreateClient();

        // The advertised mount URL is the account's own address under /webdav (#2128): tenant id, user id, then the
        // volume's name, "Name (Tenant)".
        var advertised = new Uri((await TestJson.Get(api, "/api/me/devices")).GetProperty("servers").GetProperty("webDavUrl").GetString()!);
        var userId = (await TestJson.Get(api, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();
        Assert.StartsWith($"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/{Id(tenantId)}/{Id(userId)}/", advertised.AbsolutePath);
        Assert.StartsWith("Dav User (", Uri.UnescapeDataString(advertised.AbsolutePath.Split('/')[^1]));

        // The Repositories tree-pane's top level = the "Personal" node + the repositories the user can see — minus
        // the Administration folder, which an administrator's tree shows and the mount deliberately does not (ADR
        // 1066's named exception to ADR 0509): a folder of references to other people's spaces is nothing a file
        // browser can do anything useful with. Asserted present in the tree first, so the exception is really tested.
        var repositories = (await TestJson.Get(api, "/api/repositories")).GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()!).ToList();
        Assert.Contains("Administration", repositories);
        var repos = repositories.Where(name => name != "Administration").ToHashSet();
        var expectedTopLevel = new HashSet<string>(repos) { Personal };

        // PROPFIND the single /webdav resource: root named "webdav", children == the tree top level.
        var names = await PropFindNamesAsync(dav, basic, ("/" + global::Alcifra.Presentation.WireNames.WebDavSegment));
        Assert.Contains(global::Alcifra.Presentation.WireNames.WebDavSegment, names);
        // The one named exception (#2053, ADR 1023): the hidden file saying whose mount this is, which is not a node.
        Assert.Contains(global::Alcifra.Presentation.WireNames.WebDavAccountFile, names);
        Assert.Equal(expectedTopLevel, names
            .Where(n => n != global::Alcifra.Presentation.WireNames.WebDavSegment && n != global::Alcifra.Presentation.WireNames.WebDavAccountFile)
            .ToHashSet());
        Assert.DoesNotContain("Administration", names);

        // The account's address serves the same tree (the label decides nothing), and another account's ids are refused.
        var own = await PropFindNamesAsync(dav, basic, advertised.AbsolutePath);
        Assert.Equal(expectedTopLevel, own
            .Where(n => n != global::Alcifra.Presentation.WireNames.WebDavAccountFile && n != global::Alcifra.Presentation.WireNames.WebDavSegment)
            .ToHashSet());
        using var foreign = new HttpRequestMessage(new HttpMethod("PROPFIND"),
            $"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/{Id(tenantId)}/{Id(Guid.NewGuid())}/Someone")
        { Headers = { Authorization = basic } };
        foreign.Headers.TryAddWithoutValidation("Depth", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await dav.SendAsync(foreign)).StatusCode);

        // Writes too: a file PUT and then MOVEd through the account's address, the Destination naming the same escaped
        // label, lands where the plain address sees it.
        var repo = repos.First();
        var baseAddress = advertised.AbsolutePath.TrimEnd('/');
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"{baseAddress}/{Uri.EscapeDataString(repo)}/first.txt") { Headers = { Authorization = basic }, Content = new ByteArrayContent(Encoding.UTF8.GetBytes("x")) })
        {
            Assert.True((await dav.SendAsync(put)).IsSuccessStatusCode);
        }

        using (var move = new HttpRequestMessage(new HttpMethod("MOVE"), $"{baseAddress}/{Uri.EscapeDataString(repo)}/first.txt") { Headers = { Authorization = basic } })
        {
            move.Headers.TryAddWithoutValidation("Destination", $"http://localhost{baseAddress}/{Uri.EscapeDataString(repo)}/moved.txt");
            Assert.True((await dav.SendAsync(move)).IsSuccessStatusCode);
        }

        Assert.Contains("moved.txt", await PropFindNamesAsync(dav, basic, $"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/{Uri.EscapeDataString(repo)}"));

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

    // The ids as the address carries them: 22 characters of URL-safe base64 (ADR 1043).
    private static string Id(Guid id) => System.Buffers.Text.Base64Url.EncodeToString(id.ToByteArray());

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
