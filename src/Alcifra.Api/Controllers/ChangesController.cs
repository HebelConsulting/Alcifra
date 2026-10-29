using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.Documents;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.Documents;
using Alcifra.Api.Pagination;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// "Changes since" (ADR 0925; SimplArchiveMobile #5): the documents the caller may see whose
/// <see cref="Document.ChangedAt"/> moved after a cursor, as small rows, so an offline copy refreshes only what
/// changed. Root rel <c>changes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without <c>since</c></b> the answer has no rows and a cursor at "now": a client takes it BEFORE it fills its
/// copy, so nothing that changes during the fill is missed.
/// </para>
/// <para>
/// <b>The cursor lags "now" by <see cref="SettleWindow"/>.</b> A transaction stamps before it commits, so a slow
/// commit can appear with a stamp a little in the past; rows newer than the window are left for the next call
/// rather than skipped for good. The cursor advances past every row SCANNED, visible or not, so a run of rows the
/// caller may not see is not walked again, and reveals nothing (it is a position, not a row).
/// </para>
/// <para>
/// Deleted documents are answered with <c>deleted: true</c> when the caller could see them; permission changes
/// are not stamped (the phone re-checks daily).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/changes")]
[Authorize]
public class ChangesController(
    AlcifraDbContext dbContext,
    DocumentAccessService access,
    IClearanceScopeResolver clearanceScope,
    IMaskContainmentProvider containment,
    ICurrentTenantAccessor currentTenant) : ControllerBase
{
    /// <summary>How far behind "now" the cursor stays, for transactions still committing.</summary>
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(5);

    // Plain mutable classes, not records — XmlSerializer (ADR "JSON/XML content negotiation").
    public class ChangeResource
    {
        public Guid Id { get; set; }

        public Guid? ParentId { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsFolder { get; set; }

        public bool Deleted { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public Guid? CurrentVersionId { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? VersionCreatedAt { get; set; }

        public DateTimeOffset ChangedAt { get; set; }
    }

    public class ChangeListResource : HypermediaResource
    {
        public List<ChangeResource> Changes { get; set; } = [];

        /// <summary>Where the next call continues: pass it back as <c>since</c>.</summary>
        public string Cursor { get; set; } = string.Empty;
    }

    private sealed record ChangeRow(Guid Id, Guid? ParentId, string Name, Guid? MaskId, bool HasVersions, DateTimeOffset? DeletedAt,
        Guid? CurrentVersionId, DateTimeOffset? VersionCreatedAt, DateTimeOffset ChangedAt);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? since, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var pageSize = PageSize.Resolve(limit);
        var watermark = DateTimeOffset.UtcNow - SettleWindow;
        if (since is null)
        {
            return Ok(Answer([], Cursor.Encode(watermark, Guid.Empty), since, pageSize, hasMore: false, _ => false));
        }

        if (!Cursor.TryDecode(since, out var fromAt, out var fromId))
        {
            throw new InvalidChangesCursorException();
        }

        // Deleted rows too (a copy must drop them), within the caller's clearance like every listing.
        var clearance = await clearanceScope.ResolveAsync(cancellationToken);
        var query = clearance.Filter(dbContext.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]))
            .Where(d => d.ChangedAt <= watermark);

        // Walk-and-collect (the children listing's shape): drop rows the caller may not see until the page fills.
        var page = new List<ChangeRow>(pageSize);
        var (lastAt, lastId) = (fromAt, fromId);
        var hasMore = false;
        while (true)
        {
            var (at, id) = (lastAt, lastId);
            var batch = await query
                .Where(d => d.ChangedAt > at || (d.ChangedAt == at && d.Id > id))
                .OrderBy(d => d.ChangedAt).ThenBy(d => d.Id)
                .Take(pageSize + 1)
                .Select(d => new ChangeRow(
                    d.Id,
                    d.ParentId,
                    d.Name,
                    dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault(),
                    dbContext.DocumentVersions.Any(v => v.DocumentId == d.Id),
                    d.DeletedAt,
                    // The CURRENT version as the listing resolves it (ADR 0503): the pinned one, else the newest confirmed.
                    d.CurrentVersionId != null
                        ? d.CurrentVersionId
                        : dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed)
                            .OrderByDescending(v => v.VersionNumber).Select(v => (Guid?)v.Id).FirstOrDefault(),
                    d.CurrentVersionId != null
                        ? dbContext.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault()
                        : dbContext.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed)
                            .OrderByDescending(v => v.VersionNumber).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault(),
                    d.ChangedAt))
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var rights = await access.GetCallerRightsForManyAsync([.. batch.Select(b => b.Id)], cancellationToken);
            foreach (var row in batch)
            {
                if (page.Count == pageSize)
                {
                    hasMore = true;
                    break;
                }

                (lastAt, lastId) = (row.ChangedAt, row.Id);   // scanned: the cursor moves past it, seen or not
                if (rights.TryGetValue(row.Id, out var r) && r.CanSee)
                {
                    page.Add(row);
                }
            }

            if (hasMore || batch.Count <= pageSize)
            {
                break;
            }
        }

        // Nothing newer at all: the client may move up to the watermark, so the next call starts there.
        var cursor = !hasMore && page.Count == 0 && lastAt == fromAt && lastId == fromId && watermark > fromAt
            ? Cursor.Encode(watermark, Guid.Empty)
            : Cursor.Encode(lastAt, lastId);
        var rules = await containment.ForAsync(dbContext, currentTenant.TenantId!.Value, cancellationToken);
        return Ok(Answer(page, cursor, since, pageSize, hasMore, row => rules.IsFolderRow(row.MaskId, row.HasVersions)));
    }

    [HttpHead]
    public IActionResult Head() => NoContent();

    private ChangeListResource Answer(List<ChangeRow> rows, string cursor, string? since, int pageSize, bool hasMore, Func<ChangeRow, bool> isFolder)
    {
        var links = new List<Link> { new("self", Url.Action(nameof(List), new { since, limit = pageSize })!, "GET") };
        if (hasMore)
        {
            links.Add(new Link("next", Url.Action(nameof(List), new { since = cursor, limit = pageSize })!, "GET"));
        }

        return new ChangeListResource
        {
            Changes = [.. rows.Select(r => new ChangeResource
            {
                Id = r.Id,
                ParentId = r.ParentId,
                Name = r.Name,
                IsFolder = isFolder(r),
                Deleted = r.DeletedAt is not null,
                CurrentVersionId = r.CurrentVersionId,
                VersionCreatedAt = r.VersionCreatedAt,
                ChangedAt = r.ChangedAt,
            })],
            Cursor = cursor,
            Links = links,
        };
    }
}
