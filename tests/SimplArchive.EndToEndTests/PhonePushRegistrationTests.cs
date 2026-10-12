using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A user's phones registered for push (SimplArchiveMobile #20, slice B; owner decisions 2026-10-12): registered after
// sign-in, refreshed and removed with If-Match because one user may have several clients, never shared with another
// user, and the per-type phone switch in the notification preferences.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class PhonePushRegistrationTests
{
    private readonly E2EApiFactory _factory;

    public PhonePushRegistrationTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpClient> UserAsync(Guid tenantId)
    {
        var email = $"phone-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ph-1234", "Phone User");
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "ph-1234"));
    }

    private static async Task<string> CollectionAsync(HttpClient api) =>
        (await TestJson.Get(api, "/api")).GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "pushRegistrations").GetProperty("href").GetString()!;

    private static object Device(string endpoint, string label = "Pixel · Anna on demo") =>
        new { app = "dev.simplarchive.mobile", deviceLabel = label, endpoint, p256dh = DeviceKeys.P256dh, auth = DeviceKeys.Auth };

    private static string SelfOf(JsonElement row) =>
        row.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString()!;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient api, HttpMethod method, string url, object? body, string? etag)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return await api.SendAsync(request);
    }

    [Fact]
    public async Task A_device_registers_lists_refreshes_with_its_tag_and_unregisters()
    {
        var api = await UserAsync(await _factory.SeedTenantNamedAsync($"push-{Guid.NewGuid():N}"));
        var collection = await CollectionAsync(api);
        var endpoint = $"https://push.example.test/up/{Guid.NewGuid():N}";

        using var created = await api.PostAsJsonAsync(collection, Device(endpoint));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var etag = created.Headers.ETag!.Tag;
        var self = SelfOf(await created.Content.ReadFromJsonAsync<JsonElement>());

        // Registering the same address again answers the existing row and its tag, unchanged.
        using var again = await api.PostAsJsonAsync(collection, Device(endpoint));
        Assert.Equal((HttpStatusCode.OK, etag), (again.StatusCode, again.Headers.ETag!.Tag));

        var list = await TestJson.Get(api, collection);
        Assert.Equal("Pixel · Anna on demo", Assert.Single(list.GetProperty("registrations").EnumerateArray()).GetProperty("deviceLabel").GetString());

        // A refresh needs the tag (428 without), and a stale tag is refused (412).
        using (var missing = await SendAsync(api, HttpMethod.Put, self, Device(endpoint, "Renamed"), null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        using var refreshed = await SendAsync(api, HttpMethod.Put, self, Device(endpoint, "Renamed"), etag);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        using (var stale = await SendAsync(api, HttpMethod.Put, self, Device(endpoint, "Again"), etag))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }

        using var removed = await SendAsync(api, HttpMethod.Delete, self, null, refreshed.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await TestJson.Get(api, collection)).GetProperty("registrations").EnumerateArray());
    }

    [Fact]
    public async Task Another_users_address_or_registration_is_out_of_reach()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"push-{Guid.NewGuid():N}");
        var (anna, tom) = (await UserAsync(tenantId), await UserAsync(tenantId));
        var collection = await CollectionAsync(anna);
        var endpoint = $"https://push.example.test/up/{Guid.NewGuid():N}";

        using var created = await anna.PostAsJsonAsync(collection, Device(endpoint));
        var self = SelfOf(await created.Content.ReadFromJsonAsync<JsonElement>());

        using var taken = await tom.PostAsJsonAsync(collection, Device(endpoint));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        using var notHis = await tom.GetAsync(self);
        Assert.Equal(HttpStatusCode.NotFound, notHis.StatusCode);
    }

    [Fact]
    public async Task A_registration_needs_an_https_address_and_usable_keys()
    {
        var api = await UserAsync(await _factory.SeedTenantNamedAsync($"push-{Guid.NewGuid():N}"));
        var collection = await CollectionAsync(api);

        using var plain = await api.PostAsJsonAsync(collection, Device("http://push.example.test/up/1"));
        using var badKeys = await api.PostAsJsonAsync(collection,
            new { app = "dev.simplarchive.mobile", deviceLabel = "x", endpoint = "https://push.example.test/up/2", p256dh = "BKey", auth = "auth" });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (plain.StatusCode, badKeys.StatusCode));
    }

    [Fact]
    public async Task The_phone_switch_defaults_on_and_a_client_that_does_not_send_it_leaves_it_alone()
    {
        var api = await UserAsync(await _factory.SeedTenantNamedAsync($"push-{Guid.NewGuid():N}"));
        var preferences = await TestJson.Get(api, "/api/notifications/preferences");
        var first = preferences.GetProperty("preferences")[0];
        var type = first.GetProperty("type").GetInt32();
        Assert.True(first.GetProperty("phoneEnabled").GetBoolean());

        await TestJson.Put(api, "/api/notifications/preferences", new { preferences = new[] { new { type, emailEnabled = true, phoneEnabled = false } } });
        // A desktop or web client from before the switch sends email only: the phone choice stays off.
        var after = await TestJson.Put(api, "/api/notifications/preferences", new { preferences = new[] { new { type, emailEnabled = false } } });

        var row = after.GetProperty("preferences").EnumerateArray().First(p => p.GetProperty("type").GetInt32() == type);
        Assert.Equal((false, false), (row.GetProperty("emailEnabled").GetBoolean(), row.GetProperty("phoneEnabled").GetBoolean()));
    }

    // ---- The sender (a fake push server on loopback, which only Development accepts) -----------------------------

    /// <summary>A push server on loopback that records what arrives and answers with a chosen status.</summary>
    private sealed class FakePushServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string?> _arrived = new();

        public FakePushServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = $"http://127.0.0.1:{port}/push/{Guid.NewGuid():N}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        _arrived.Enqueue(context.Request.Headers["Content-Encoding"]);
                        context.Response.StatusCode = Answer;
                        context.Response.Close();
                    }
                    catch (Exception) when (!_listener.IsListening)
                    {
                    }
                }
            });
        }

        public string Endpoint { get; }

        public int Answer { get; set; } = 201;

        public int Count => _arrived.Count;

        public IReadOnlyList<string?> Encodings => [.. _arrived];

        public async Task<bool> WaitForAsync(int count)
        {
            for (var i = 0; i < 100 && Count < count; i++)
            {
                await Task.Delay(100);
            }

            return Count >= count;
        }

        public void Dispose() => _listener.Close();
    }

    private async Task<(HttpClient Anna, HttpClient Tom, Guid TomId, Guid Document)> MentionSetupAsync()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"push-{Guid.NewGuid():N}");
        var annaEmail = $"anna-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, annaEmail, "ph-1234", "Anna", canManageRepositories: true);
        var tomEmail = $"tom-{Guid.NewGuid():N}@e2e.local";
        var tomId = await _factory.SeedUserAsync(tenantId, tomEmail, "ph-1234", "Tom", isTenantAdmin: true);
        var anna = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(annaEmail, "ph-1234"));
        var tom = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(tomEmail, "ph-1234"));
        var repository = (await TestJson.Post(anna, "/api/repositories", new { name = $"repo-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var document = (await TestJson.Post(anna, $"/api/documents/{repository}/children", new { name = "Contract" })).GetProperty("id").GetGuid();
        return (anna, tom, tomId, document);
    }

    [Fact]
    public async Task A_mention_reaches_the_registered_phone_encrypted_and_a_switched_off_type_does_not()
    {
        var (anna, tom, tomId, document) = await MentionSetupAsync();
        using var phone = new FakePushServer();
        using (var registered = await tom.PostAsJsonAsync(await CollectionAsync(tom), Device(phone.Endpoint)))
        {
            registered.EnsureSuccessStatusCode();
        }

        await TestJson.Post(anna, $"/api/documents/{document}/chat", new { body = $"@[{tomId}] please check" });

        Assert.True(await phone.WaitForAsync(1), "no push arrived for the mention");
        Assert.Equal("aes128gcm", phone.Encodings[0]);   // RFC 8291: encrypted to the device, in the body alone

        // Tom switches the phone off for mentions: the next one stays on the server.
        var preferences = await TestJson.Get(tom, "/api/notifications/preferences");
        await TestJson.Put(tom, "/api/notifications/preferences", new
        {
            preferences = preferences.GetProperty("preferences").EnumerateArray().Select(p => new
            {
                type = p.GetProperty("type").GetInt32(),
                emailEnabled = true,
                phoneEnabled = p.GetProperty("typeName").GetString() != "ChatMentioned",
            }).ToList(),
        });
        await TestJson.Post(anna, $"/api/documents/{document}/chat", new { body = $"@[{tomId}] and again" });
        await Task.Delay(2000);
        Assert.Equal(1, phone.Count);
    }

    [Fact]
    public async Task A_push_server_that_answers_gone_removes_the_registration()
    {
        var (anna, tom, tomId, document) = await MentionSetupAsync();
        using var phone = new FakePushServer { Answer = 410 };
        var collection = await CollectionAsync(tom);
        using (var registered = await tom.PostAsJsonAsync(collection, Device(phone.Endpoint)))
        {
            registered.EnsureSuccessStatusCode();
        }

        await TestJson.Post(anna, $"/api/documents/{document}/chat", new { body = $"@[{tomId}] are you there" });

        Assert.True(await phone.WaitForAsync(1));
        for (var i = 0; i < 50 && (await TestJson.Get(tom, collection)).GetProperty("registrations").GetArrayLength() > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Empty((await TestJson.Get(tom, collection)).GetProperty("registrations").EnumerateArray());
    }
}
