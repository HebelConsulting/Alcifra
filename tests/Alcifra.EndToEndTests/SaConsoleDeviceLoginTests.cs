using Alcifra.ApiClient.Login;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Alcifra.EndToEndTests;

// alcifra's own device-flow client against a real server (ADR 0823), exercising THE CLI's code rather than
// a re-implementation of it.
//
// That distinction is the point. `DeviceAuthorizationFlowTests` drives the protocol with hand-written HTTP and
// proves the SERVER is right; it would pass just as happily if alcifra's polling loop ignored `interval`,
// mistook `slow_down` for a failure, or retried after a person had said no. A replay built by analogy replays
// its author's assumptions — so this one calls DeviceFlow, the type the shipped tool uses.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SaConsoleDeviceLoginTests
{
    private readonly E2EApiFactory _factory;

    public SaConsoleDeviceLoginTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_cli_requests_a_code_waits_for_approval_and_receives_a_usable_token()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"cli-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "cli-1234", "CLI Admin");

        using var transport = _factory.CreateClient();
        var flow = new DeviceFlow(transport, "alcifra-cli");

        // 1. The tool asks for a code, exactly as `alcifra login` does.
        var authorization = await flow.RequestAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(authorization.UserCode));
        Assert.False(string.IsNullOrEmpty(authorization.DeviceCode));
        Assert.Contains("connect/verify", authorization.VerificationUri, StringComparison.Ordinal);

        // RFC 8628 §3.2 makes `interval` optional and specifies 5 seconds when absent. A zero here would mean
        // the shipped tool polls in a tight loop against a live installation.
        Assert.True(authorization.Interval > TimeSpan.Zero, "the client must never poll with no interval");
        Assert.True(authorization.ExpiresAt > DateTimeOffset.UtcNow);

        // 2. A person approves, in a browser, while the tool polls — so the poll really does start against an
        //    unapproved code and has to wait, which is the behaviour under test.
        var approval = Task.Run(async () =>
        {
            await ApproveAsync(authorization.UserCode, email, "cli-1234");
        });

        var token = await flow.PollAsync(authorization, _ => { }, CancellationToken.None);
        await approval;

        Assert.False(string.IsNullOrEmpty(token));

        // 3. The token the CLI ended up with is accepted AS that user — the only check that proves the whole
        //    chain rather than the protocol's shape.
        using var tool = _factory.CreateAuthedClient(token);
        var whoami = await TestJson.Get(tool, "/api/diagnostics/whoami");
        Assert.Equal(userId, whoami.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task A_refusal_reaches_the_cli_as_a_message_rather_than_a_retry()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"clino-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "cli-1234", "Refusing CLI Admin");

        using var transport = _factory.CreateClient();
        var flow = new DeviceFlow(transport, "alcifra-cli");
        var authorization = await flow.RequestAsync(CancellationToken.None);

        var refusal = Task.Run(async () =>
        {
            await ApproveAsync(authorization.UserCode, email, "cli-1234", handler: "Refuse");
        });

        // access_denied must STOP the loop. A client that treated it as another "keep waiting" would poll a
        // dead code until it expired, and pester somebody who had already answered.
        var error = await Assert.ThrowsAsync<DeviceFlowException>(
            () => flow.PollAsync(authorization, _ => { }, CancellationToken.None));
        await refusal;

        Assert.Contains("refused", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The session file against a real server (ADR 1071): the login carries a refresh token, a command renews it, and a
    // second renewal proves the token still knows when the session began, which the hard limit reads.
    [Theory]
    [InlineData("alcifra-cli")]
    [InlineData("alcifra-pass")]
    public async Task A_command_renews_the_session_kept_in_the_file(string clientId)
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"clisession-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "cli-1234", "CLI Session");

        using var transport = _factory.CreateClient();
        var flow = new DeviceFlow(transport, clientId);
        var authorization = await flow.RequestAsync(CancellationToken.None);
        var approval = Task.Run(() => ApproveAsync(authorization.UserCode, email, "cli-1234"));
        var login = await flow.PollForSessionAsync(authorization, _ => { }, CancellationToken.None);
        await approval;
        Assert.False(string.IsNullOrEmpty(login.RefreshToken));

        var path = Path.Combine(Path.GetTempPath(), $"cli-session-{Guid.NewGuid():N}", "session.json");
        var url = transport.BaseAddress!.ToString().TrimEnd('/');
        var session = new CliSession(path, (request, cancellationToken) => transport.SendAsync(request, cancellationToken));
        session.Save(url, clientId, login);

        var seen = login.RefreshToken;
        for (var renewal = 0; renewal < 2; renewal++)
        {
            // Aged past "renewed moments ago", which would otherwise rightly skip the request.
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(session.Read()! with { RenewedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));

            var state = await session.ResolveAsync(null, null, renew: true);

            Assert.False(string.IsNullOrEmpty(state!.AccessToken));
            Assert.NotEqual(seen, state.RefreshToken);
            seen = state.RefreshToken;
            using var tool = _factory.CreateAuthedClient(state.AccessToken!);
            Assert.Equal(userId, (await TestJson.Get(tool, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid());
        }

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    // What the hard limit does not catch: somebody copied the file and renews from it. The next use of a spent token,
    // whoever's it was, is refused, and the session in use stops renewing with it.
    [Fact]
    public async Task A_spent_refresh_token_presented_again_ends_the_session()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"clicopy-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "cli-1234", "CLI Copy");

        using var transport = _factory.CreateClient();
        var flow = new DeviceFlow(transport, "alcifra-pass");
        var authorization = await flow.RequestAsync(CancellationToken.None);
        var approval = Task.Run(() => ApproveAsync(authorization.UserCode, email, "cli-1234"));
        var login = await flow.PollForSessionAsync(authorization, _ => { }, CancellationToken.None);
        await approval;

        async Task<(HttpStatusCode Status, string? Refresh)> RenewAsync(string token)
        {
            using var response = await transport.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token,
                ["client_id"] = "alcifra-pass",
            }));
            var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            return (response.StatusCode, body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null);
        }

        var (first, current) = await RenewAsync(login.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, first);

        // Past the leeway the test host sets for parallel commands.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var (replayed, _) = await RenewAsync(login.RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, replayed);

        var (followed, _) = await RenewAsync(current!);
        Assert.Equal(HttpStatusCode.BadRequest, followed);
    }

    /// <summary>Drives the real browser journey: sign in, then approve or refuse the code.</summary>
    private async Task ApproveAsync(string userCode, string email, string password, string handler = "Approve")
    {
        var browser = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var challenge = await browser.GetAsync($"/connect/verify?user_code={Uri.EscapeDataString(userCode)}");
        var loginPath = challenge.Headers.Location!.ToString();

        var loginHtml = await browser.GetStringAsync(loginPath);
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();

        var login = await browser.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = Token(loginHtml),
        }));

        var response = await browser.GetAsync(login.Headers.Location!.ToString());
        for (var i = 0; i < 4 && response.Headers.Location is { } next; i++)
        {
            response = await browser.GetAsync(next.ToString());
        }

        var page = await response.Content.ReadAsStringAsync();
        var form = Regex.Match(page, $@"<form[^>]*handler={handler}[^>]*>.*?</form>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(form.Success, $"the {handler} form is not on the approval page");

        using var answer = await browser.PostAsync($"/connect/verify?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user_code"] = userCode,
                ["c"] = Regex.Match(form.Value, @"name=""c""[^>]*value=""([^""]*)""").Groups[1].Value,
                ["__RequestVerificationToken"] = Token(form.Value),
            }));

        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
    }

    private static string Token(string html) =>
        Regex.Match(html, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
}
