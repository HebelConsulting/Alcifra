using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Alcifra.EndToEndTests;

// #2053: every user and tenant of an installation mount the same WebDAV address, and an operating system lists a mounted
// volume by that address alone, so the root carries one hidden, read-only file naming whose mount it is. These pin what a
// client relies on: each user's mount names that user and tenant, it is listed hidden, and nobody can change it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class WebDavAccountFileTests
{
    private static readonly string Root = $"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}";
    private static readonly string File = $"{Root}/{(global::Alcifra.Presentation.WireNames.WebDavAccountFile)}";

    private readonly E2EApiFactory _factory;

    public WebDavAccountFileTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(Guid TenantId, string Email, AuthenticationHeaderValue Basic)> UserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"mount-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "mount-1234", "Mount User");
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "mount-1234"));
        var password = (await TestJson.Post(api, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
        return (tenantId, email, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}"))));
    }

    [Fact]
    public async Task Each_users_mount_names_that_user_and_tenant()
    {
        var anna = await UserAsync();
        var tom = await UserAsync();   // another tenant: the same address, a different account
        using var dav = _factory.CreateClient();

        foreach (var (tenantId, email, basic) in new[] { anna, tom })
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, File) { Headers = { Authorization = basic } };
            using var response = await dav.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal($"{email}\n{tenantId:D}\n", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_root_lists_it_hidden_and_read_only()
    {
        var (_, _, basic) = await UserAsync();
        using var dav = _factory.CreateClient();
        using var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), Root) { Headers = { Authorization = basic } };
        propfind.Headers.TryAddWithoutValidation("Depth", "1");

        var xml = await (await dav.SendAsync(propfind)).Content.ReadAsStringAsync();

        Assert.Contains($"<D:displayname>{(global::Alcifra.Presentation.WireNames.WebDavAccountFile)}</D:displayname>", xml);
        Assert.Contains("<Z:Win32FileAttributes xmlns:Z=\"urn:schemas-microsoft-com:\">00000003</Z:Win32FileAttributes>", xml);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("MOVE")]
    [InlineData("LOCK")]
    [InlineData("PROPPATCH")]
    public async Task Nobody_can_change_it(string method)
    {
        var (_, _, basic) = await UserAsync();
        using var dav = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), File)
        {
            Headers = { Authorization = basic },
            Content = new StringContent("someone-else@e2e.local\n"),
        };
        request.Headers.TryAddWithoutValidation("Destination", $"{Root}/renamed");

        Assert.Equal(HttpStatusCode.Forbidden, (await dav.SendAsync(request)).StatusCode);
    }
}
