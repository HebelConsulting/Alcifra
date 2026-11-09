using System.Xml.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.Search;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Infrastructure.Search;

namespace Alcifra.Api.Controllers;

/// <summary>
/// Admin-triggered full search-index rebuild (ADR 0139 / 0253's deferred reindex-all): backfills documents
/// that predate indexing (or were missed while OpenSearch was down) via a blue-green alias swap. Restricted
/// to a PlatformAdministrator — the index spans every tenant, so this is platform maintenance, not a
/// per-tenant action (like TenantsController/PlatformAdministratorsController). The rebuild runs in a
/// background hosted service; POST queues a run and returns 202 with it, GET reports the latest one, and each run
/// answers at its own address. The runs live in the database, so either instance reports the same (ADR 1081).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/search/reindex")]
[Authorize]
public class SearchReindexController : ControllerBase
{
    private readonly SearchReindexState _state;
    private readonly ICurrentPlatformAdministratorAccessor _platformAdministratorAccessor;
    private readonly AlcifraDbContext _dbContext;
    private readonly OpenSearchIndexRebuilder? _rebuilder;

    // The rebuilder is null when OpenSearch is not configured (its registration is conditional) — the health
    // fields then answer "not applicable" rather than the controller failing to construct.
    public SearchReindexController(
        SearchReindexState state, ICurrentPlatformAdministratorAccessor platformAdministratorAccessor,
        AlcifraDbContext dbContext, OpenSearchIndexRebuilder? rebuilder = null)
    {
        _state = state;
        _platformAdministratorAccessor = platformAdministratorAccessor;
        _dbContext = dbContext;
        _rebuilder = rebuilder;
    }

    // The latest rebuild with the index's health (GET), or the run a POST queued. A run's own address answers the
    // same shape, so `alcifra search reindex --wait` follows one link (ADR 1081).
    public class ReindexResource : HypermediaResource
    {
        // idle (no run yet), queued, running, succeeded or failed — of the run this resource describes.
        public string Status { get; set; } = string.Empty;

        [XmlElement(IsNullable = true)]
        public Guid? RunId { get; set; }

        [XmlElement(IsNullable = true)]
        public string? Trigger { get; set; }

        [XmlElement(IsNullable = true)]
        public DateTimeOffset? RequestedAt { get; set; }

        [XmlElement(IsNullable = true)]
        public DateTimeOffset? StartedAt { get; set; }

        [XmlElement(IsNullable = true)]
        public DateTimeOffset? FinishedAt { get; set; }

        public int Attempts { get; set; }

        // The reason a failed run gave up: the exception's type and message, nothing more.
        [XmlElement(IsNullable = true)]
        public string? Failure { get; set; }

        // The instance that ran it, so a run left open by an instance that died can be named.
        [XmlElement(IsNullable = true)]
        public string? Instance { get; set; }

        // Documents indexed by the last completed rebuild, or -1 if none has finished yet.
        public int LastIndexedCount { get; set; }

        // Whether the "documents" alias is MISSING (#661): while true, every per-document write is gated off
        // and every search answers empty — the degradation ADR 0626 forbids leaving invisible, now stated
        // where an administrator can ask for it instead of only in one startup warning. Null when OpenSearch
        // is not configured (the Postgres fallback needs no alias) or unreachable at the moment of asking.
        [XmlElement(IsNullable = true)]
        public bool? AliasMissing { get; set; }

        // Outbox rows waiting to be indexed (#661). A number that keeps growing names its own problem —
        // including the deliberate hold while a rebuild runs, which is what makes the pause observable.
        public int PendingOutbox { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Trigger(CancellationToken cancellationToken)
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        if (_rebuilder is null)
        {
            throw new SearchReindexNotConfiguredException();
        }

        var run = await SearchReindexRuns.RequestAsync(_dbContext, SearchReindexTrigger.Requested, DateTimeOffset.UtcNow, cancellationToken);
        _state.Request();
        return Accepted(await DescribeAsync(run, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return Ok(await DescribeAsync(await SearchReindexRuns.LatestAsync(_dbContext, cancellationToken), cancellationToken));
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> Run(Guid runId, CancellationToken cancellationToken)
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var run = await SearchReindexRuns.FindAsync(_dbContext, runId, cancellationToken) ?? throw new SearchReindexRunNotFoundException();
        return Ok(await DescribeAsync(run, cancellationToken));
    }

    private async Task<ReindexResource> DescribeAsync(SearchReindexRun? run, CancellationToken cancellationToken)
    {
        var lastSucceeded = await SearchReindexRuns.LastIndexedCountAsync(_dbContext, cancellationToken);
        var resource = new ReindexResource
        {
            Status = run switch
            {
                null => "idle",
                { FinishedAt: null, StartedAt: null } => "queued",
                { FinishedAt: null } => "running",
                { Failure: not null } => "failed",
                _ => "succeeded",
            },
            RunId = run?.Id,
            Trigger = run?.Trigger.ToString(),
            RequestedAt = run?.RequestedAt,
            StartedAt = run?.StartedAt,
            FinishedAt = run?.FinishedAt,
            Attempts = run?.Attempts ?? 0,
            Failure = run?.Failure,
            Instance = run?.Instance,
            LastIndexedCount = lastSucceeded ?? -1,
            AliasMissing = await AliasMissingAsync(cancellationToken),
            PendingOutbox = await _dbContext.SearchIndexOutbox.CountAsync(cancellationToken), // not tenant-scoped by design
        };
        if (run is not null)
        {
            resource.Links.Add(new Link("self", $"/api/search/reindex/runs/{run.Id}", "GET"));
        }

        return resource;
    }

    private async Task<bool?> AliasMissingAsync(CancellationToken cancellationToken)
    {
        if (_rebuilder is null)
        {
            return null; // OpenSearch not configured — the Postgres fallback has no alias to miss
        }

        try
        {
            return !await _rebuilder.AliasExistsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null; // unreachable right now — unknown, not "missing" (which would read as data loss)
        }
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => _platformAdministratorAccessor.PlatformAdministratorId is null ? Forbid() : NoContent();

    [HttpHead("runs/{runId:guid}")]
    public async Task<IActionResult> HeadRun(Guid runId, CancellationToken cancellationToken)
    {
        if (_platformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return await SearchReindexRuns.FindAsync(_dbContext, runId, cancellationToken) is null ? NotFound() : NoContent();
    }
}
