using System.Net;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// Renewal as the shared library does it for both native apps (ADR 0917): the bearer on every request, a refresh
// shortly before expiry with the HOST's client id, one replay after a 401, and a clean end when renewal cannot work.
// The session store is the host's seam; here the in-memory one.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class RenewalTests : ApiClientSettingsTest
{
    private const string Root = "https://archive.example.test";
    private readonly InMemoryTokenSessionStore _store = new();

    public RenewalTests()
    {
        ApiClientSettings.ClientId = "simplarchive-mobile";
        ApiClientSettings.Sessions = _store;
    }

    private static HttpMessageInvoker Client(TokenSession session, FakeHandler handler) =>
        new(new RenewingAuthHandler(Root, new TokenSession.Holder(session), handler));

    private static HttpRequestMessage Get() => new(HttpMethod.Get, $"{Root}/api");

    private static HttpResponseMessage Tokens(string access, string refresh) =>
        FakeHandler.Json($$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":900}""");

    [Fact]
    public async Task Every_request_carries_the_sessions_bearer()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Client(new TokenSession("access-1", null, DateTimeOffset.MaxValue), handler).SendAsync(Get(), default);

        Assert.Equal("Bearer access-1", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task A_token_about_to_expire_is_renewed_first_with_the_hosts_client_id_and_stored()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/connect/token" ? Tokens("access-2", "refresh-2") : new HttpResponseMessage(HttpStatusCode.OK));

        await Client(new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(10)), handler).SendAsync(Get(), default);

        var (method, uri, _, body) = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal($"{Root}/connect/token", uri.ToString());
        Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", body, StringComparison.Ordinal);
        Assert.Contains("client_id=simplarchive-mobile", body, StringComparison.Ordinal);
        Assert.Equal("Bearer access-2", handler.Requests[1].Authorization);
        Assert.Equal("refresh-2", _store.For(Root)?.RefreshToken);   // rotated, and persisted through the host's seam
    }

    [Fact]
    public async Task A_401_is_renewed_and_the_request_replayed_once()
    {
        var calls = 0;
        var handler = new FakeHandler(r =>
            r.RequestUri!.AbsolutePath == "/connect/token" ? Tokens("access-2", "refresh-2")
            : ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Client(new TokenSession("access-1", "refresh-1", DateTimeOffset.MaxValue), handler).SendAsync(Get(), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Bearer access-1", null, "Bearer access-2"], handler.Requests.Select(r => r.Authorization));
    }

    [Fact]
    public async Task A_401_with_no_way_to_renew_ends_the_session_and_forgets_it()
    {
        _store.Set(Root, new TokenSession("access-1", null, DateTimeOffset.MaxValue));
        string? ended = null;
        void OnEnded(string root) => ended = root;
        RenewingAuthHandler.SessionEnded += OnEnded;
        try
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var thrown = await Assert.ThrowsAsync<SessionEndedException>(() =>
                Client(new TokenSession("access-1", null, DateTimeOffset.MaxValue), handler).SendAsync(Get(), default));

            Assert.Equal(Root, thrown.ApiRootUrl);
            Assert.Equal(Root, ended);
            Assert.Null(_store.For(Root));
        }
        finally
        {
            RenewingAuthHandler.SessionEnded -= OnEnded;
        }
    }

    [Fact]
    public async Task A_refused_renewal_ends_the_session_rather_than_retrying_for_ever()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/connect/token"
            ? FakeHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<SessionEndedException>(() =>
            Client(new TokenSession("access-1", "refresh-1", DateTimeOffset.MaxValue), handler).SendAsync(Get(), default));

        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsolutePath == "/connect/token"));
    }

    [Fact]
    public async Task A_handler_with_its_own_store_renews_into_it_and_leaves_the_hosts_alone()
    {
        // A phone's push for a profile that is not the one on screen (mobile #20): the rotated refresh token belongs in
        // THAT profile's slot. Written into the host's store it would land in the on-screen profile's slot instead.
        var own = new InMemoryTokenSessionStore();
        _store.Set(Root, new TokenSession("foreground", "foreground-refresh", DateTimeOffset.MaxValue));
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/connect/token" ? Tokens("access-2", "refresh-2") : new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpMessageInvoker(new RenewingAuthHandler(
            Root, new TokenSession.Holder(new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow)), handler, own, announce: false));

        await client.SendAsync(Get(), default);

        Assert.Equal("refresh-2", own.For(Root)?.RefreshToken);
        Assert.Equal("foreground-refresh", _store.For(Root)?.RefreshToken);
    }

    [Fact]
    public async Task A_quiet_handler_ends_its_own_session_without_raising_the_hosts_prompt()
    {
        var own = new InMemoryTokenSessionStore();
        own.Set(Root, new TokenSession("access-1", null, DateTimeOffset.MaxValue));
        _store.Set(Root, new TokenSession("foreground", null, DateTimeOffset.MaxValue));
        var raised = false;
        void OnEnded(string _) => raised = true;
        RenewingAuthHandler.SessionEnded += OnEnded;
        try
        {
            var client = new HttpMessageInvoker(new RenewingAuthHandler(
                Root, new TokenSession.Holder(own.For(Root)!), new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), own, announce: false));

            await Assert.ThrowsAsync<SessionEndedException>(() => client.SendAsync(Get(), default));

            Assert.False(raised);
            Assert.Null(own.For(Root));
            Assert.NotNull(_store.For(Root));
        }
        finally
        {
            RenewingAuthHandler.SessionEnded -= OnEnded;
        }
    }

    [Fact]
    public void A_bound_client_needs_a_stored_session_and_does_not_take_over_the_hosts_content_client()
    {
        var own = new InMemoryTokenSessionStore();
        Assert.Null(ApiCore.Bound(Root, own));

        var before = ApiCore.Authenticated;
        own.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(15)));
        var bound = ApiCore.Bound(Root, own);

        Assert.NotNull(bound);
        Assert.Equal("access-1", bound.AccessToken);
        Assert.Equal(new Uri($"{Root}/"), bound.Http.BaseAddress);
        Assert.Same(before, ApiCore.Authenticated);
    }

    [Fact]
    public void A_session_says_when_it_needs_renewing_and_whether_it_can()
    {
        Assert.True(new TokenSession("a", "r", DateTimeOffset.UtcNow.AddSeconds(30)).NeedsRenewal);   // inside the minute ahead
        Assert.False(new TokenSession("a", "r", DateTimeOffset.UtcNow.AddHours(1)).NeedsRenewal);
        Assert.False(new TokenSession("a", null, DateTimeOffset.UtcNow).CanRenew);
        Assert.True(new TokenSession("a", "r", DateTimeOffset.UtcNow).CanRenew);
    }

    [Fact]
    public void The_in_memory_store_keys_sessions_by_installation_ignoring_case()
    {
        var store = new InMemoryTokenSessionStore();
        var session = new TokenSession("a", "r", DateTimeOffset.MaxValue);

        store.Set("https://Archive.Example.test", session);

        Assert.Same(session, store.For("https://archive.example.test"));
        store.Clear("https://ARCHIVE.example.test");
        Assert.Null(store.For("https://archive.example.test"));
    }

    // ---- Several clients of ONE slot (#1869) -------------------------------------------------------------------------
    // The phone holds several clients of one profile at once: the page on screen, a push's fetch, the scheduled backup.
    // Each used to copy the session when it was built, so one renewal spent the refresh token the others still held, and
    // one ended session cleared the slot under the others ("a 401 without any attempt to renew" on the emulator).

    /// <summary>A server that rotates strictly: each refresh token redeems once, then is refused (no leeway).</summary>
    private sealed class RotatingServer : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _valid = new(StringComparer.Ordinal);
        private string _live = "refresh-1";
        private int _issued = 1;

        public int Refreshes { get; private set; }

        public List<string?> Bearers { get; } = [];

        /// <summary>Runs as a request arrives, before it is answered: where a test makes something happen meanwhile.</summary>
        public Func<HttpRequestMessage, string?, Task>? OnArrival { get; set; }

        public void Accept(string refreshToken, string accessToken)
        {
            lock (_lock)
            {
                _live = refreshToken;
                _issued = int.Parse(refreshToken["refresh-".Length..], System.Globalization.CultureInfo.InvariantCulture);
                _valid.Add(accessToken);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var presented = System.Web.HttpUtility.ParseQueryString(body)["refresh_token"];
            if (OnArrival is { } arrival)
            {
                await arrival(request, presented);
            }

            if (request.RequestUri!.AbsolutePath == "/connect/token")
            {
                await Task.Delay(50, cancellationToken);   // long enough for every other client to arrive at the gate
                lock (_lock)
                {
                    if (!string.Equals(presented, _live, StringComparison.Ordinal))
                    {
                        return FakeHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                    }

                    Refreshes++;
                    _issued++;
                    _live = $"refresh-{_issued}";
                    _valid.Add($"access-{_issued}");
                    return Tokens($"access-{_issued}", _live);
                }
            }

            var bearer = request.Headers.Authorization?.Parameter;
            lock (_lock)
            {
                Bearers.Add(bearer);
                return new HttpResponseMessage(bearer is not null && _valid.Contains(bearer) ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
            }
        }
    }

    private static HttpMessageInvoker ClientOf(ITokenSessionStore slot, HttpMessageHandler server) =>
        new(new RenewingAuthHandler(Root, TokenSession.Holder.Stored(slot, Root), server, slot, announce: false));

    [Fact]
    public async Task Clients_of_one_slot_renew_once_between_them_and_all_carry_on()
    {
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow));   // due: every client renews first
        var server = new RotatingServer();
        var clients = Enumerable.Range(0, 4).Select(_ => ClientOf(slot, server)).ToList();

        var responses = await Task.WhenAll(clients.SelectMany(c => new[] { c.SendAsync(Get(), default), c.SendAsync(Get(), default) }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, server.Refreshes);
        Assert.All(server.Bearers, b => Assert.Equal("access-2", b));
        Assert.Equal("refresh-2", slot.For(Root)?.RefreshToken);
    }

    /// <summary>A view of one slot, as the phone's <c>ProfileSessions.Of(profile)</c> is: a new object per call.</summary>
    private sealed class SlotView(ITokenSessionStore inner) : ITokenSessionStore
    {
        public TokenSession? For(string apiRootUrl) => inner.For(apiRootUrl);

        public void Set(string apiRootUrl, TokenSession session) => inner.Set(apiRootUrl, session);

        public void Clear(string apiRootUrl) => inner.Clear(apiRootUrl);
    }

    [Fact]
    public async Task Clients_over_separate_views_of_one_slot_still_renew_once()
    {
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow));
        var server = new RotatingServer();

        var responses = await Task.WhenAll(
            ClientOf(new SlotView(slot), server).SendAsync(Get(), default),
            ClientOf(new SlotView(slot), server).SendAsync(Get(), default));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, server.Refreshes);
    }

    [Fact]
    public async Task A_client_built_before_another_renewed_sends_the_renewed_session()
    {
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow));
        var server = new RotatingServer();
        var onScreen = ClientOf(slot, server);
        var push = ClientOf(slot, server);

        await push.SendAsync(Get(), default);
        var response = await onScreen.SendAsync(Get(), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.Refreshes);   // the page on screen did not present the refresh token the push had spent
        Assert.Equal(["access-2", "access-2"], server.Bearers);
    }

    [Fact]
    public async Task A_401_for_a_token_another_client_has_since_replaced_replays_with_the_new_one()
    {
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(10)));
        var server = new RotatingServer();
        var client = ClientOf(slot, server);
        server.OnArrival = (request, _) =>
        {
            // While this request is on the wire, another client of the slot renews: access-1 is refused, access-7 works.
            if (request.Headers.Authorization?.Parameter == "access-1")
            {
                server.Accept("refresh-7", "access-7");
                slot.Set(Root, new TokenSession("access-7", "refresh-7", DateTimeOffset.UtcNow.AddMinutes(15)));
            }

            return Task.CompletedTask;
        };

        var response = await client.SendAsync(Get(), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, server.Refreshes);   // refresh-7 is not spent a second time
        Assert.Equal(["access-1", "access-7"], server.Bearers);
        Assert.Equal("refresh-7", slot.For(Root)?.RefreshToken);
    }

    [Fact]
    public async Task A_refused_renewal_tries_the_session_somebody_else_stored_meanwhile_before_ending_it()
    {
        // Somebody outside the gate (another process over the same store, a fresh sign-in of the profile) renewed while
        // this client's refresh was on the wire: refresh-1 is refused, and the slot already holds refresh-5.
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow));
        var server = new RotatingServer();
        server.OnArrival = (_, presented) =>
        {
            if (presented == "refresh-1")
            {
                server.Accept("refresh-5", "access-5");
                slot.Set(Root, new TokenSession("access-5", "refresh-5", DateTimeOffset.UtcNow));   // due as well
            }

            return Task.CompletedTask;
        };

        var response = await ClientOf(slot, server).SendAsync(Get(), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.Refreshes);
        Assert.Equal("refresh-6", slot.For(Root)?.RefreshToken);
    }

    [Fact]
    public async Task A_session_one_client_ended_is_ended_for_every_client_of_the_slot()
    {
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow));
        var refused = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/connect/token"
            ? FakeHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var first = ClientOf(slot, refused);
        var second = ClientOf(slot, refused);

        await Assert.ThrowsAsync<SessionEndedException>(() => first.SendAsync(Get(), default));
        await Assert.ThrowsAsync<SessionEndedException>(() => second.SendAsync(Get(), default));

        Assert.Null(slot.For(Root));
        Assert.Equal(1, refused.Requests.Count(r => r.Uri.AbsolutePath == "/connect/token"));   // the second did not try again
    }

    [Fact]
    public void Bound_clients_of_one_slot_read_one_session()
    {
        var slot = new InMemoryTokenSessionStore();
        slot.Set(Root, new TokenSession("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(15)));
        var onScreen = ApiCore.Bound(Root, slot)!;
        var push = ApiCore.Bound(Root, slot)!;

        slot.Set(Root, new TokenSession("access-2", "refresh-2", DateTimeOffset.UtcNow.AddMinutes(15)));   // either renewed

        Assert.Equal("access-2", onScreen.AccessToken);
        Assert.Equal("access-2", push.AccessToken);
    }
}
