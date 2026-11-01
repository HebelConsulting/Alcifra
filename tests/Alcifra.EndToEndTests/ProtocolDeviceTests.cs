using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailKit.Net.Imap;
using MailKit.Security;
using Alcifra.Api.Imap;

namespace Alcifra.EndToEndTests;

// #1970 slice 1, end to end: a device's own password reaches exactly the protocols its switches open, revoking one
// device leaves the others working, the earlier passwords keep working beside enrolled devices, and the API refuses
// what the owner decided it refuses (mail encryption without a hardware key, a second device of the same name, editing
// an earlier password). Reached through me's protocolDevices rel, as a client would.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ProtocolDeviceTests
{
    private readonly E2EApiFactory _factory;

    public ProtocolDeviceTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, string Email, string Devices)> SignedInUserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"devices-{Guid.NewGuid():N}@e2e.local";
        const string password = "devices-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Device User");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var me = await TestJson.Get(api, "/api/me");
        var devices = me.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "protocolDevices").GetProperty("href").GetString()!;
        return (api, email, devices);
    }

    private static AuthenticationHeaderValue Basic(string email, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}")));

    private async Task<HttpStatusCode> PropfindAsync(string path, AuthenticationHeaderValue credentials)
    {
        using var dav = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = credentials } };
        request.Headers.TryAddWithoutValidation("Depth", "0");
        return (await dav.SendAsync(request)).StatusCode;
    }

    private async Task<bool> ImapSignsInAsync(string email, string password)
    {
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        try
        {
            await client.AuthenticateAsync(email, password);
            await client.DisconnectAsync(true);
            return true;
        }
        catch (AuthenticationException)
        {
            return false;
        }
    }

    private static string WebDav => "/" + global::Alcifra.Presentation.WireNames.WebDavSegment;

    [Fact]
    public async Task A_device_password_reaches_only_the_protocols_its_switches_open()
    {
        var (api, email, devices) = await SignedInUserAsync();
        using var _ = api;

        var enrolled = await TestJson.Post(api, devices,
            new { name = "Files only", mail = false, files = true, calendars = false, contacts = false });
        var password = enrolled.GetProperty("password").GetString()!;
        Assert.Matches(@"^[a-z2-7]{8}\.[0-9a-f]{32}$", password);
        Assert.Equal(email, enrolled.GetProperty("username").GetString());

        var credentials = Basic(email, password);
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync(WebDav, credentials));
        Assert.Equal(HttpStatusCode.Unauthorized, await PropfindAsync("/caldav/", credentials));
        Assert.Equal(HttpStatusCode.Unauthorized, await PropfindAsync("/carddav/", credentials));
        Assert.False(await ImapSignsInAsync(email, password));

        // A login it is allowed is recorded as its last use.
        var self = enrolled.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "self")
            .GetProperty("href").GetString()!;
        Assert.NotEqual(JsonValueKind.Null, (await TestJson.Get(api, self)).GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task Revoking_one_device_leaves_another_working()
    {
        var (api, email, devices) = await SignedInUserAsync();
        using var _ = api;

        var laptop = await TestJson.Post(api, devices, new { name = "Laptop" });
        var phone = await TestJson.Post(api, devices, new { name = "Phone" });

        using var revoke = new HttpRequestMessage(HttpMethod.Delete,
            laptop.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString());
        revoke.Headers.TryAddWithoutValidation("If-Match", laptop.GetProperty("eTag").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(revoke)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await PropfindAsync(WebDav, Basic(email, laptop.GetProperty("password").GetString()!)));
        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync(WebDav, Basic(email, phone.GetProperty("password").GetString()!)));
        Assert.True(await ImapSignsInAsync(email, phone.GetProperty("password").GetString()!));
    }

    [Fact]
    public async Task An_earlier_password_keeps_working_beside_enrolled_devices_and_can_only_be_revoked()
    {
        var (api, email, devices) = await SignedInUserAsync();
        using var _ = api;

        // An earlier password exists only as the migration or the demo seed made it: seeded the same way here, since the
        // endpoint that issued one is retired (slice 3).
        const string earlier = "0123456789abcdef0123456789abcdef";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Alcifra.Infrastructure.Persistence.AlcifraDbContext>();
            var user = await db.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant());
            db.ProtocolDevices.Add(Alcifra.Api.ProtocolDevices.LegacyProtocolDevices.New(user, Alcifra.Domain.Users.ProtocolDeviceKind.LegacyDav,
                new Microsoft.AspNetCore.Identity.PasswordHasher<Alcifra.Domain.Users.User>().HashPassword(user, earlier), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await TestJson.Post(api, devices, new { name = "Laptop" });

        Assert.Equal(HttpStatusCode.MultiStatus, await PropfindAsync(WebDav, Basic(email, earlier)));

        var list = await TestJson.Get(api, devices);
        var legacy = list.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("kind").GetString() == "earlierDav");
        Assert.False(legacy.GetProperty("canEdit").GetBoolean());

        using var edit = new HttpRequestMessage(HttpMethod.Put,
            legacy.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString())
        {
            Content = JsonContent.Create(new { name = "Renamed" }),
        };
        edit.Headers.TryAddWithoutValidation("If-Match", legacy.GetProperty("eTag").GetString());
        var refused = await api.SendAsync(edit);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("PROTOCOL_DEVICE_NOT_EDITABLE", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_hand_over_gives_the_profile_once_to_whoever_holds_the_link()
    {
        var (api, email, devices) = await SignedInUserAsync();
        using var _ = api;

        var enrolled = await TestJson.Post(api, devices, new { name = "New phone" });
        var password = enrolled.GetProperty("password").GetString()!;
        var handOver = enrolled.GetProperty("handOver");
        Assert.StartsWith("data:image/png;base64,", handOver.GetProperty("passwordQr").GetString());
        Assert.StartsWith("data:image/png;base64,", handOver.GetProperty("profileQr").GetString());
        var link = new Uri(handOver.GetProperty("profileUrl").GetString()!).PathAndQuery;

        // A phone's camera browser: not signed in. The token in the link is the authorisation.
        using var camera = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await camera.SendAsync(new HttpRequestMessage(HttpMethod.Head, link))).StatusCode);

        var first = await camera.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/x-apple-aspen-config", first.Content.Headers.ContentType!.MediaType);
        Assert.True(first.Headers.CacheControl!.NoStore);
        var profile = await first.Content.ReadAsStringAsync();
        Assert.Contains(password, profile);
        Assert.Contains(email, profile);

        Assert.Equal(HttpStatusCode.NotFound, (await camera.GetAsync(link)).StatusCode);
    }

    [Fact]
    public async Task A_files_only_device_gets_no_profile_but_its_password_and_addresses()
    {
        var (api, _, devices) = await SignedInUserAsync();
        using var _ = api;

        var handOver = (await TestJson.Post(api, devices,
            new { name = "Files", mail = false, files = true, calendars = false, contacts = false })).GetProperty("handOver");

        Assert.Equal(JsonValueKind.Null, handOver.GetProperty("profileUrl").ValueKind);
        Assert.StartsWith("data:image/png;base64,", handOver.GetProperty("passwordQr").GetString());
        Assert.EndsWith("/" + global::Alcifra.Presentation.WireNames.WebDavSegment, handOver.GetProperty("servers").GetProperty("webDavUrl").GetString());
    }

    [Fact]
    public async Task The_API_refuses_what_was_decided_with_its_reason()
    {
        var (api, _, devices) = await SignedInUserAsync();
        using var __ = api;

        // Mail encryption without an attested hardware key: refused, named.
        var encrypted = await api.PostAsJsonAsync(devices, new { name = "Sealed", encryptMail = true });
        Assert.Equal(HttpStatusCode.Conflict, encrypted.StatusCode);
        Assert.Contains("DEVICE_ENCRYPTION_NEEDS_HARDWARE_KEY", await encrypted.Content.ReadAsStringAsync());

        // A second device of the same name.
        var first = await TestJson.Post(api, devices, new { name = "Tablet" });
        var twin = await api.PostAsJsonAsync(devices, new { name = " Tablet " });
        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        Assert.Contains("PROTOCOL_DEVICE_NAME_TAKEN", await twin.Content.ReadAsStringAsync());

        // A change states the precondition of what was read.
        var self = first.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "self")
            .GetProperty("href").GetString()!;
        var withoutIfMatch = await api.PutAsJsonAsync(self, new { name = "Tablet 2" });
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);
    }
}
