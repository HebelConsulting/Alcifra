using System.Net;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Login;

namespace Alcifra.UnitTests;

// The session file both tools share (ADR 1071): the shell's variables win, a command renews it, a refusal ends it, and an
// unreachable server does not.
public sealed class CliSessionTests : IDisposable
{
    private const string Url = "https://archive.example";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"cli-session-{Guid.NewGuid():N}");
    private int _requests;

    private string SessionPath => Path.Combine(_folder, "nested", "session.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private CliSession Answering(HttpStatusCode status, string body = "{}") => new(SessionPath, (_, _) =>
    {
        _requests++;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    });

    private void Stored(TimeSpan age, string? refresh = "refresh-1")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, JsonSerializer.Serialize(
            new CliSessionState(Url, "alcifra-pass", "access-1", refresh, DateTimeOffset.UtcNow - age)));
    }

    private const string Renewed = """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":900}""";

    [Fact]
    public async Task A_command_renews_the_session_and_keeps_the_rotated_token()
    {
        Stored(TimeSpan.FromMinutes(5));
        var session = Answering(HttpStatusCode.OK, Renewed);

        var state = await session.ResolveAsync(null, null, renew: true);

        Assert.Equal("access-2", state!.AccessToken);
        Assert.Equal("refresh-2", session.Read()!.RefreshToken);
        Assert.Equal(1, _requests);
    }

    [Fact]
    public async Task A_session_renewed_moments_ago_is_used_as_it_is()
    {
        Stored(TimeSpan.FromSeconds(1));

        var state = await Answering(HttpStatusCode.OK, Renewed).ResolveAsync(null, null, renew: true);

        Assert.Equal("access-1", state!.AccessToken);
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task Completion_costs_no_request_and_extends_nothing()
    {
        Stored(TimeSpan.FromMinutes(5));

        var state = await Answering(HttpStatusCode.OK, Renewed).ResolveAsync(null, null, renew: false);

        Assert.Equal("access-1", state!.AccessToken);
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task A_token_in_the_shell_wins_and_the_file_is_left_alone()
    {
        Stored(TimeSpan.FromMinutes(5));

        Assert.Null(await Answering(HttpStatusCode.OK, Renewed).ResolveAsync(Url, "shell-token", renew: true));
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task A_url_in_the_shell_naming_another_installation_never_receives_the_files_token()
    {
        Stored(TimeSpan.FromMinutes(5));

        Assert.Null(await Answering(HttpStatusCode.OK, Renewed).ResolveAsync("https://other.example", null, renew: true));
        Assert.NotNull(await Answering(HttpStatusCode.OK, Renewed).ResolveAsync($"{Url}/", null, renew: true));
    }

    [Fact]
    public async Task A_refused_renewal_ends_the_session_and_keeps_where_it_was()
    {
        Stored(TimeSpan.FromMinutes(20));

        var state = await Answering(HttpStatusCode.BadRequest).ResolveAsync(null, null, renew: true);

        Assert.Equal(Url, state!.Url);
        Assert.Null(state.AccessToken);
        Assert.Null(state.RefreshToken);
    }

    [Fact]
    public async Task An_unreachable_server_changes_nothing()
    {
        Stored(TimeSpan.FromMinutes(5));
        var session = Answering(HttpStatusCode.BadGateway);

        var state = await session.ResolveAsync(null, null, renew: true);

        Assert.Equal("access-1", state!.AccessToken);
        Assert.Equal("refresh-1", session.Read()!.RefreshToken);
    }

    [Fact]
    public async Task A_session_from_an_older_installation_has_nothing_to_renew_with()
    {
        Stored(TimeSpan.FromMinutes(5), refresh: null);

        var state = await Answering(HttpStatusCode.OK, Renewed).ResolveAsync(null, null, renew: true);

        Assert.Equal("access-1", state!.AccessToken);
        Assert.Equal(0, _requests);
    }

    [Fact]
    public void Only_the_owner_can_read_the_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new CliSession(SessionPath).Save($"{Url}/", "alcifra-pass", new TokenSession("a", "r", DateTimeOffset.UtcNow));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SessionPath));
        Assert.Equal(Url, new CliSession(SessionPath).Read()!.Url);
    }

    [Fact]
    public async Task Ending_the_session_removes_it_even_where_the_server_cannot_be_reached()
    {
        var session = new CliSession(SessionPath);
        session.Save("http://127.0.0.1:1", "alcifra-pass", new TokenSession("a", "r", DateTimeOffset.UtcNow));

        Assert.Equal("http://127.0.0.1:1", await session.EndAsync());
        Assert.False(File.Exists(SessionPath));
        Assert.Null(await session.EndAsync());
    }

    [Fact]
    public void A_login_warns_only_where_the_shell_would_override_it()
    {
        Assert.Null(CliSession.ShellOverrideWarning(null, null, Url));
        Assert.Null(CliSession.ShellOverrideWarning(Url, null, Url));
        Assert.Contains("unset", CliSession.ShellOverrideWarning(null, "token", Url));
        Assert.Contains("unset", CliSession.ShellOverrideWarning("https://other.example", null, Url));
    }
}
