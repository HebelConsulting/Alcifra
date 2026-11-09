namespace Alcifra.Infrastructure.Search;

// One search-index rebuild, from its request to its outcome (ADR 1081). It lives in the database rather than in an
// instance's memory because the installation runs more than one instance (ADR 0808): a rebuild asked of one is
// reported by whichever answers next, so `alcifra search reindex --wait` can follow it behind the proxy.
//
// Deliberately NOT ITenantScoped: the index spans every tenant, as the outbox does. And NOT IConcurrencyTracked,
// an exemption the owner granted 2026-11-08: the row is MACHINE-OWNED. The endpoint creates it and the rebuild
// service claims and closes it; no request edits one, so there is no lost update to lose (ADR 0878).
public sealed class SearchReindexRun
{
    public Guid Id { get; set; }

    public SearchReindexTrigger Trigger { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    // Null = queued. Set by the CLAIM of the instance that runs it (one compare-and-swap on `StartedAt IS NULL`,
    // ADR 0836), so two instances woken for one request do not both rebuild.
    public DateTimeOffset? StartedAt { get; set; }

    // Set when it ended, whether it succeeded (IndexedCount) or not (Failure).
    public DateTimeOffset? FinishedAt { get; set; }

    public int? IndexedCount { get; set; }

    public int Attempts { get; set; }

    // One line: the exception's type and message, never a payload or a secret.
    public string? Failure { get; set; }

    // The instance that claimed it, so a run left open by an instance that died can be named.
    public string? Instance { get; set; }
}

public enum SearchReindexTrigger
{
    // A platform administrator asked for it (POST /api/search/reindex).
    Requested = 0,

    // The service found the alias missing or the mapping stale at startup.
    Startup = 1,
}
