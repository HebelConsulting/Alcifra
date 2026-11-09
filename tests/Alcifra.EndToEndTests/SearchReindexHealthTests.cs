using System.Net;
using System.Text.Json;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.EndToEndTests;

// Index health is observable (#661): GET /api/search/reindex says whether the alias is MISSING (while it is,
// every search answers empty — the degradation ADR 0626 forbids leaving invisible) and how many outbox rows
// are pending. Before this the resource carried only a status string and a count from the last rebuild, so
// "search is permanently empty" and "search is fine" read identically to an administrator.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SearchReindexHealthTests
{
    private readonly E2EApiFactory _factory;

    public SearchReindexHealthTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_reindex_status_carries_alias_and_outbox_health()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var status = await TestJson.Get(admin, "/api/search/reindex");

        // The fixture boots with OpenSearch configured and the startup backfill complete, so the alias
        // exists — false, not null (null means "not configured / unreachable", a different answer).
        Assert.Equal(JsonValueKind.False, status.GetProperty("aliasMissing").ValueKind);

        // The fixture also waits for the outbox to drain before handing the app over (#660), so pending is a
        // real number and normally zero. Asserted non-negative rather than zero: a concurrently-running test
        // in this collection may legitimately have rows in flight.
        Assert.True(status.GetProperty("pendingOutbox").GetInt32() >= 0);
        Assert.False(string.IsNullOrEmpty(status.GetProperty("status").GetString()));
    }

    // #1715, ADR 1081: `alcifra search reindex --wait` follows the run the POST answered to its end. The run is a row,
    // so the status is the same whichever instance answers; here the same steps the command takes, against the server.
    [Fact]
    public async Task A_requested_rebuild_is_followed_to_its_end_by_its_own_address()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var http = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var api = new AlcifraApi(http);

        var run = await SearchReindexSurface.RequestAsync(api, CancellationToken.None);
        var runId = SearchReindexSurface.Text(run, "runId");
        Assert.False(string.IsNullOrEmpty(runId));
        Assert.Contains(SearchReindexSurface.Text(run, "status"), new[] { "queued", "running" });

        var done = await SearchReindexSurface.FollowAsync(
            api, run, DateTimeOffset.UtcNow.AddMinutes(3), TimeSpan.FromMilliseconds(250), _ => { }, CancellationToken.None);

        Assert.Equal("succeeded", SearchReindexSurface.Text(done, "status"));
        Assert.Equal(runId, SearchReindexSurface.Text(done, "runId"));
        Assert.True(done.GetProperty("attempts").GetInt32() >= 1);
        Assert.True(done.GetProperty("lastIndexedCount").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.String, done.GetProperty("finishedAt").ValueKind);

        // The run outlives the request: a later status names it, unless a newer one was asked for meanwhile.
        var status = await SearchReindexSurface.StatusAsync(api, CancellationToken.None);
        Assert.Equal(JsonValueKind.String, status.GetProperty("runId").ValueKind);
    }

    [Fact]
    public async Task An_unknown_run_answers_404()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var admin = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        using var response = await admin.GetAsync($"/api/search/reindex/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("SEARCH_REINDEX_RUN_NOT_FOUND", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_tenant_principal_is_refused_the_platform_surface()
    {
        // The index spans every tenant, so this is platform maintenance — a tenant's own admin has no
        // business reading (or triggering) it.
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var tenant = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync("/api/search/reindex")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostAsync("/api/search/reindex", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.GetAsync($"/api/search/reindex/runs/{Guid.NewGuid()}")).StatusCode);
    }
}
