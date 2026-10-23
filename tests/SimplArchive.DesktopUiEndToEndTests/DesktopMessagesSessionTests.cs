using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;
using SimplArchive.Messenger.Client.Display;

namespace SimplArchive.UiEndToEndTests;

// The Messages tab and the session it talks through (2026-10-23, desktop 0.50.0 against the demo): the logon window's
// session never renewed, so it ended after the access token's 15 minutes; the sign-in from the "session ended" modal
// did not change IsLoggedIn, so the tab kept the Messenger client of the ENDED session, sent no bearer, and every click
// on it put the modal back up. A fake archive on a loopback port records which bearer each request carried; the
// self-hosted app runs no Messenger, and the question is the client, not the module.
[Collection(UiCollection.Name)]
public sealed class DesktopMessagesSessionTests : IDisposable
{
    private readonly string _apiBaseUrl = DesktopClientOptions.ApiBaseUrl;
    private readonly ISecretStore? _secrets = SecretStores.Override;
    private readonly FakeArchive _archive = new();

    public DesktopMessagesSessionTests()
    {
        SecretStores.Override = new InMemorySecretStore();   // never the developer's keychain
        DesktopClientOptions.ApiBaseUrl = _archive.BaseUrl;
    }

    public void Dispose()
    {
        _archive.Dispose();
        TokenSessions.Current.Clear(_archive.BaseUrl);
        DesktopClientOptions.ApiBaseUrl = _apiBaseUrl;
        SecretStores.Override = _secrets;
    }

    [Fact]
    public async Task After_a_sign_in_the_tab_talks_through_the_new_session_never_the_one_it_was_discovered_with()
    {
        var current = new SimplArchiveApiClient("token-first");
        var tab = new MessagesTabViewModel(new TestShell(), () => current, NoLiveEvents.Instance);
        await tab.DiscoverAsync();
        Assert.True(tab.IsOffered);

        // Signed in again with the window signed in throughout: nothing tells the tab, as from the "session ended" modal.
        current = new SimplArchiveApiClient("token-second");
        await tab.LoadAsync();

        Assert.Equal(["Bearer token-second"], _archive.BearersOf(FakeArchive.ConversationsPath));
        Assert.True(tab.IsOffered);
    }

    [Fact]
    public async Task A_session_that_was_reset_is_not_used_again_by_anything_the_tab_does()
    {
        SimplArchiveApiClient? current = new SimplArchiveApiClient("token-first");
        var tab = new MessagesTabViewModel(new TestShell(), () => current, NoLiveEvents.Instance);
        await tab.DiscoverAsync();

        tab.Reset();
        current = new SimplArchiveApiClient("token-second");
        await tab.LoadAsync();   // not rediscovered yet: does nothing, and certainly not with the first session
        Assert.Empty(_archive.BearersOf(FakeArchive.ConversationsPath));

        await tab.DiscoverAsync();
        await tab.LoadAsync();
        Assert.Equal(["Bearer token-second"], _archive.BearersOf(FakeArchive.ConversationsPath));
    }

    // A refusal by a server that answers is not a lost connection: no modal, which would not fix it and come back on
    // every click, but the status line and a Warning in the log (ADR 0626).
    [Fact]
    public async Task A_refused_conversation_list_is_reported_in_the_status_line_rather_than_thrown_to_the_crash_guard()
    {
        _archive.RefuseConversations = true;
        var shell = new TestShell();
        var api = new SimplArchiveApiClient("token-first");
        var tab = new MessagesTabViewModel(shell, () => api, NoLiveEvents.Instance);
        await tab.DiscoverAsync();

        await tab.LoadAsync();   // must not throw

        var prefix = Strings.Get("MsgLoadFailed").Replace("{0}", string.Empty);
        Assert.Contains(shell.Reports, r => r.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Empty(tab.Conversations);
    }

    // The logon window is how a session normally begins, and it handed over a client seeded from the bare access
    // token, so nothing ever renewed. Now its client adopts the recorded session and renews ahead of expiry.
    [Fact]
    public async Task A_sign_in_at_the_logon_window_records_its_session_so_the_client_renews()
    {
        var profiles = Path.Combine(Path.GetTempPath(), $"logon-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = profiles;
        try
        {
            var vm = new LogonViewModel();
            var server = new ServerProfile { Name = "Fake", ApiRootUrl = _archive.BaseUrl };
            vm.Servers.Add(server);
            vm.SelectedServer = server;
            vm.ReachabilityCheck = (_, _) => Task.FromResult(true);
            vm.Authenticate = (_, _) => Task.FromResult<AuthResult?>(
                new AuthResult("token-first", "anna@example.com", "refresh-first", DateTimeOffset.UtcNow.AddSeconds(30)));
            SimplArchiveApiClient? api = null;
            vm.LoginSucceeded += (signedIn, _) => api = signedIn;

            await ((IAsyncRelayCommand)vm.LoginCommand).ExecuteAsync(null);
            Assert.NotNull(api);
            await api!.GetRootLinksAsync();   // due within the minute: renewed first

            Assert.Contains(_archive.Requests, r => r is { Method: "POST", Path: "/connect/token" } && r.Body.Contains("refresh_token=refresh-first", StringComparison.Ordinal));
            Assert.Equal(["Bearer token-renewed"], _archive.BearersOf("/api"));
            Assert.Equal("refresh-renewed", TokenSessions.Current.For(_archive.BaseUrl)?.RefreshToken);
        }
        finally
        {
            ServerProfileStore.PathOverride = null;
            File.Delete(profiles);
        }
    }

    /// <summary>An archive root offering the Messenger, a whoami and the conversation list; records every bearer.</summary>
    private sealed class FakeArchive : IDisposable
    {
        public const string ConversationsPath = "/api/modules/messenger/conversations";

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public FakeArchive()
        {
            var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public bool RefuseConversations { get; set; }

        public List<(string Method, string Path, string? Bearer, string Body)> Requests { get; } = [];

        public List<string?> BearersOf(string path)
        {
            lock (Requests)
            {
                return [.. Requests.Where(r => r.Method == "GET" && r.Path == path).Select(r => r.Bearer)];
            }
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var (method, path) = (context.Request.HttpMethod, context.Request.Url!.AbsolutePath);
                lock (Requests)
                {
                    Requests.Add((method, path, context.Request.Headers["Authorization"], body));
                }

                var (status, json) = (method, path) switch
                {
                    ("GET", "/api") => (200, $$"""{"links":[{"rel":"messenger:conversations","href":"{{ConversationsPath}}","method":"GET"},{"rel":"whoami","href":"/api/diagnostics/whoami","method":"GET"}]}"""),
                    ("GET", "/api/diagnostics/whoami") => (200, $$"""{"userId":"{{Guid.NewGuid()}}","userName":"Anna"}"""),
                    ("GET", ConversationsPath) when RefuseConversations => (403, """{"title":"Forbidden","errorCode":"MESSENGER_NOT_ACTIVE"}"""),
                    ("GET", ConversationsPath) => (200, """{"items":[],"links":[]}"""),
                    ("POST", "/connect/token") => (200, """{"access_token":"token-renewed","refresh_token":"refresh-renewed","expires_in":900}"""),
                    _ => (404, "{}"),
                };
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }
    }
}
