using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors;
using Alcifra.Api.Errors.Exceptions.Ocr;
using Alcifra.Api.Errors.Exceptions.Documents;
using Alcifra.Api.Errors.Exceptions.Concurrency;
using Alcifra.Api.Errors.Exceptions.Checkout;
using Alcifra.Api.Errors.Exceptions.LegalHolds;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.Pagination;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The document's relationship listings and child creation: children (list/create) and referencing-folders.
/// Split out of DocumentsController (#466); routes unchanged — ListChildren/CreateChild check rights against
/// the parent document itself, since that is the container being read/written (ADR 0209).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public partial class DocumentChildrenController : ControllerBase
{
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly AlcifraDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;
    private readonly IDocumentIndexQueue _queue;
    private readonly Documents.IClearanceScopeResolver _clearanceScope;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    // Shared with the SaveChanges invariant, so what this offers and what that permits are the same answer
    // rather than two that agree today (#673, ADR 0655).
    private readonly IMaskContainmentProvider _containment;

    // The loaded modules and a logger, for the name vocabularies a module's masks may declare (ABI 0.21).
    // Both are optional in effect: with no module loaded the lookup is empty and every entry keeps a plain
    // name box.
    private readonly IReadOnlyList<Infrastructure.Modules.ModuleLoader.LoadedModule> _modules;
    private readonly ILogger<DocumentChildrenController> _logger;

    // Module-declared DAV kinds (#1242) — see the create affordance below.
    private readonly IDavCollectionKindRegistry _davKinds;

    // Writes a new child's initial index data in the same save as the child (#1634), through the one writer every
    // index-data path shares rather than a copy of it.
    private readonly Documents.IndexDataWriter _indexData;

    // A new store or Sealed document gets its first audience in the same commit (ADR 0983).
    private readonly Audience.ContentAudience _audience;
    private readonly Audience.EncryptedForCaller _encryptedFor;

    public DocumentChildrenController(
        ICurrentUserAccessor currentUserAccessor,
        AlcifraDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit,
        IDocumentIndexQueue queue,
        Documents.IClearanceScopeResolver clearanceScope,
        ICurrentTenantAccessor currentTenantAccessor,
        IMaskContainmentProvider containment,
        IReadOnlyList<Infrastructure.Modules.ModuleLoader.LoadedModule> modules,
        IDavCollectionKindRegistry davKinds,
        ILogger<DocumentChildrenController> logger,
        Documents.IndexDataWriter indexData,
        Audience.ContentAudience audience,
        Audience.EncryptedForCaller encryptedFor)
    {
        _indexData = indexData;
        _audience = audience;
        _encryptedFor = encryptedFor;
        _modules = modules;
        _logger = logger;
        _davKinds = davKinds;
        _currentUserAccessor = currentUserAccessor;
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
        _queue = queue;
        _clearanceScope = clearanceScope;
        _currentTenantAccessor = currentTenantAccessor;
        _containment = containment;
    }

    public class DocumentSummaryResource : HypermediaResource, Hypermedia.ICarriesRowCapabilities
    {
        /// <summary>May the caller DELETE this row? (#858)</summary>
        /// <remarks>
        /// A flag, not a rel: <c>DELETE</c> is at this item's own address, so a <c>delete</c> rel beside
        /// <c>self</c> would be the same URL under a second name (ADR 0719). Absence means the same as a
        /// missing rel — not available to you, here, now (ADR 0543) — so a client disables Delete rather than
        /// offering it and handling a 403.
        /// </remarks>
        public bool CanDelete { get; set; }

        /// <summary>May the caller rename this row, or change its index data or contents order? (#858)</summary>
        /// <remarks>Named for the RIGHT the <c>PUT</c> enforces, so the gate and the refusal cannot drift.</remarks>
        public bool CanEditIndexData { get; set; }

        /// <summary>May this item be moved? (#858)</summary>
        /// <remarks>
        /// Says the ITEM may be moved, never that a given move will succeed: a move also needs
        /// <c>CanCreateSubItems</c> on the TARGET, which no row can answer before a target is chosen — the
        /// picker owns that half (ADR 0689).
        /// </remarks>
        public bool CanMove { get; set; }

        /// <summary>May the caller manage this row's permissions? (#858)</summary>
        public bool CanManagePermissions { get; set; }

        /// <summary>May the caller file a NEW VERSION of this row — the content right the versions POST enforces?</summary>
        /// <remarks>Named for the RIGHT, like <see cref="CanEditIndexData"/>; mobile #346's document picker gates on it.</remarks>
        public bool CanEditContent { get; set; }

        /// <summary>May the caller create a plain child inside this row? (#854)</summary>
        /// <remarks>Policy AND right — see <c>ICarriesRowCapabilities.CanCreateChildren</c>.</remarks>
        public bool CanCreateChildren { get; set; }

        /// <summary>
        /// For encrypted content only (ADR 0983): <c>you</c>, <c>not-you</c> ("not encrypted for you") or
        /// <c>not-yet-you</c> ("not yet encrypted for you"); null on every other row. See <c>EncryptedForCaller</c>.
        /// </summary>
        public string? EncryptedForCaller { get; set; }

        /// <summary>A password entry that carries a verification code, by its index (ADR 1013); false on every other row.</summary>
        public bool HasCode { get; set; }

        /// <summary>A password entry that holds a passkey, by its index (ADR 1033); false on every other row.</summary>
        public bool HasPasskey { get; set; }


        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        // Presentation metadata (ADR "Blazor repository/document browsing", "Workbench pane content fixes"):
        // HasVersions picks the icon (folder when false, downloadable document when true — a folder is a
        // Document with zero versions); HasChildren = anything filed here at all — a child document, a subfolder,
        // OR a reference filed into it (issue #376; it counted only child Documents before, so a folder holding
        // only shortcuts reported false); HasSubfolders = a child that is itself a folder, or a shortcut to one,
        // which is what governs the folder tree's expand caret (the tree shows folders and folder shortcuts).
        // Computed, never stored.
        public bool HasChildren { get; set; }

        public bool HasVersions { get; set; }

        // A folder by its MASK, not by "has no file" (#1708): a file-less record is a document. Clients open,
        // select and draw a row by this; HasVersions only says whether content exists.
        public bool IsFolder { get; set; }

        public bool HasSubfolders { get; set; }

        // True when at least one DocumentReference targets this item — drives the "References …" affordance.
        // See ADR "References-of-an-item list". Computed in SQL, never stored.
        public bool HasReferences { get; set; }

        // True when this item is DIRECTLY in an active legal hold (ADR "Legal hold & retention enforcement") —
        // drives the lock indicator. A cheap subquery; a descendant frozen only via an ancestor hold won't set
        // this on its own row (but its mutations are still refused). Computed in SQL, never stored.
        public bool OnLegalHold { get; set; }

        // Check-out state (ADR "Document check-out / check-in") — CheckedOut drives the lock glyph, CheckedOutByMe
        // distinguishes the caller's own lock. Computed, never stored.
        public bool CheckedOut { get; set; }

        public bool CheckedOutByMe { get; set; }

        // The display name of the lock holder (ADR "Check-out working-copy stash") — the clients prefix a
        // checked-out row with "[name] ". Empty when not checked out.
        public string CheckedOutByName { get; set; } = string.Empty;

        // The latest confirmed version's file extension (e.g. ".zip") — Document.Name is a bare stem (ADR 0277),
        // so the client reads the type from here (e.g. to browse a zip). Empty for folders / version-less docs.
        public string FileExtension { get; set; } = string.Empty;

        // List-row columns (ADR "List-row columns and sorting") — the document type (the assigned mask's name;
        // "" for a folder / unclassified), the latest confirmed version's document date + byte size, and the
        // tags. All derived/projected, never new stored columns.
        public string DocumentType { get; set; } = string.Empty;

        public DateOnly? DocumentDate { get; set; }

        public TimeOnly? DocumentTime { get; set; }

        public long? SizeBytes { get; set; }

        public List<string> Tags { get; set; } = [];

        // Who filed the current version, falling back to who created the document (#768). A NAME, not an id:
        // the column is read by a person, and a listing that returned an id would make every client fetch the
        // user per row — one request per row, which is what ADR 0557 exists to prevent.
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>
        /// The kinds of child this folder will accept, each with the address that creates one (#673).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Inline rather than behind a rel, because a context menu opens on a right-click and a round trip
        /// there is a visible pause on the one interaction that must feel instant. It is a value that already
        /// travels with the listing, which is exactly where ADR 0557 says to take it from.
        /// </para>
        /// <para>
        /// Empty for anything that is not a folder, and for a folder that accepts nothing — and that emptiness
        /// is meaningful in the same way a missing rel is (ADR 0543): the client shows no "New …" entries
        /// rather than offering ones the server would refuse.
        /// </para>
        /// </remarks>
        public List<CreatableChild> Admits { get; set; } = [];

        /// <summary>What to DRAW for this row — a token from the mask, or null for the shape default.</summary>
        /// <remarks>
        /// A token rather than an icon name, because the two clients draw from different icon sets. Null and
        /// unrecognised both mean "use the folder/document glyph you always used", so a row is never worse off
        /// than before this existed.
        /// </remarks>
        public string? Icon { get; set; }

        // The data-classification sensitivity label (ADR "Configurable sensitivity labels + upload defaults") —
        // the list-row badge: the label id (null = None, no badge), its name + colour. Derived, never stored here.
        public Guid? SensitivityLabelId { get; set; }

        public string SensitivityLabelName { get; set; } = string.Empty;

        public string? SensitivityLabelColor { get; set; }

        // Count of confirmed versions — gates the desktop "Compare versions" action (needs >= 2 to have anything
        // to diff), ADR "Compare-versions gating + default". Projected (one COUNT subquery), never stored.
        public int VersionCount { get; set; }

        // The latest confirmed version's CreatedAt (its filing timestamp) — the sort key for the "Created" folder
        // contents-sort order (ADR "Per-folder contents sort order"). Null for a folder / version-less doc.
        public DateTimeOffset? VersionCreatedAt { get; set; }
    }

    /// <summary>The listed folder itself — its sort order, and what the `create-child` rel needs to decide.</summary>
    private record FolderRow(FolderContentsSortOrder ContentsSortOrder, Guid? MaskId, bool IsPersonalRoot);


    public class DocumentChildrenResource : HypermediaResource
    {
        public List<DocumentSummaryResource> Children { get; set; } = [];

        // The listed folder's persisted default contents sort order (ADR "Per-folder contents sort order"). The
        // clients apply it (folders-first) as the default order when the folder is opened; a column-header click
        // is an ephemeral override. Serialized as the int enum value (Name=0/DocumentDate=1/Created=2).
        public FolderContentsSortOrder ContentsSortOrder { get; set; }

        /// <summary>May the caller create a plain child in the folder this collection belongs to? (#854)</summary>
        /// <remarks>
        /// The collection's own `create-child` rel, converted: it pointed at this very collection's address and
        /// differed from `self` only by method (ADR 0719). The reason it exists at all is the one its removed
        /// comment gave — a caller that reached a folder through its collection rather than through its
        /// parent's listing must get the SAME answer to "can I create here?" as the row would give (ADR 0637).
        /// </remarks>
        public bool CanCreateChildren { get; set; }
    }

    // "children", not "documents" — /documents/{id}/documents would repeat the same word despite being
    // nominally consistent with /repositories/{id}/documents (a Repository -> documents relationship is
    // container-to-item, while Document -> children is item-to-item, so the two were never perfectly
    // parallel anyway). See ADR "Nested Document creation".
    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet("children")]
    public async Task<IActionResult> ListChildren(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        // Fetch the listed folder's persisted contents sort order (ADR "Per-folder contents sort order") — also
        // serves as the existence check (null = no such document).
        // Also reads what the `create-child` rel below needs — the folder's own mask, and whether it is a
        // personal root. In this query rather than a second one: it already reads this row, and the rel is
        // about THIS folder, not about the children.
        var folder = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new FolderRow(
                d.ContentsSortOrder,
                _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault(),
                d.PersonalOfUserId != null))
            .FirstOrDefaultAsync(cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        var folderSortOrder = folder.ContentsSortOrder;

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        // Clearance enforcement (ADR "Sensitivity clearance enforcement"): drop children labelled above the
        // caller's clearance so they're hidden from listings entirely. No-op unless the tenant enforces it.
        var clearance = await _clearanceScope.ResolveAsync(cancellationToken);
        var query = clearance.Filter(_dbContext.Documents.Where(d => d.ParentId == documentId));

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(d => d.CreatedAt > cursorCreatedAt || (d.CreatedAt == cursorCreatedAt && d.Id > cursorId));
        }

        // Per-row visibility (ADR 0765): a child the caller cannot SEE is not listed — a name is data too,
        // and WebDAV has filtered this way since its hardening, so listing what the mount hides was a
        // standing ADR 0509 parity violation. Pagination is walk-and-collect (the repositories listing's
        // shape): fetch batches, drop invisible rows, until the page fills and one more visible row proves
        // hasMore. The rights batch is the SAME one the capability flags need, so the filter costs no extra
        // queries — the flags are stamped from it below instead of re-resolving.
        // HasChildren/HasVersions are computed in SQL (two .Any() subqueries per row) — never stored. See
        // ADR "Blazor repository/document browsing".
        var page = new List<DocumentSummaryRow>(pageSize);
        var rightsByRow = new Dictionary<Guid, Application.Abstractions.EffectiveRights>(pageSize);
        var hasMore = false;
        var scan = query;
        while (true)
        {
            var batch = await scan
                .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
                .Take(pageSize + 1)
                .AsSummaryRows(_dbContext)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var batchRights = await _access.GetCallerRightsForManyAsync([.. batch.Select(b => b.Id)], cancellationToken);
            foreach (var row in batch)
            {
                if (!batchRights.TryGetValue(row.Id, out var r) || !r.CanSee)
                {
                    continue;
                }

                if (page.Count == pageSize)
                {
                    hasMore = true;
                    break;
                }

                page.Add(row);
                rightsByRow[row.Id] = r;
            }

            if (hasMore || batch.Count <= pageSize)
            {
                break; // the page is proven full, or the folder is exhausted
            }

            var last = batch[^1];
            scan = query.Where(d => d.CreatedAt > last.CreatedAt || (d.CreatedAt == last.CreatedAt && d.Id > last.Id));
        }

        // Tags per row (ADR "List-row columns and sorting") — a single batched query over the page's ids.
        var tagsByDoc = await DocumentSummaryQueries.TagsForAsync(_dbContext, page.Select(p => p.Id).ToList(), cancellationToken);

        var links = new List<Link>
        {
            new("self", Url.Action(nameof(ListChildren), new { documentId, cursor, limit = pageSize })!, "GET"),
        };

        // `create-child` was advertised here UNCONDITIONALLY, which is the same bug the row-level rel was added
        // to fix, one level up: it offered a create on a Notebook, on an ephemeral staging folder and on a
        // personal space's first level, all of which the server refuses (#634).
        //
        // It matters more than a stray link, because this rel and the row's are now ONE name. A caller that
        // reached the same folder through its collection rather than through its parent's listing would
        // otherwise get the opposite answer to the same question — which is precisely the drift that having one
        // rel for one create is meant to remove (ADR 0637).

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(ListChildren), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        // Loaded ONCE for the whole page, not per row: the provider caches per request, so this is one query
        // however many rows the page holds — and the same object the invariant will consult if any of these
        // creates is actually attempted.
        var rules = await _containment.ForAsync(_dbContext, _currentTenantAccessor.TenantId!.Value, cancellationToken);
        // Once for the page, beside the rules, for the same reason they are: a per-row resolution would mean
        // an activation query per listed folder (ADR 0557 — one read, many follows).
        var nameVocabularies = await Documents.ModuleVocabularies.NamesForTenantAsync(
            _modules, _dbContext, _logger, DateTimeOffset.UtcNow, cancellationToken);

        var children = page.Select(d => new DocumentSummaryResource
        {
            Id = d.Id,
            Name = d.Name,
            HasChildren = d.HasChildren,
            HasVersions = d.HasVersions,
            IsFolder = rules.IsFolderRow(d.MaskId, d.HasVersions),
            HasSubfolders = d.HasSubfolders,
            HasReferences = d.HasReferences,
            OnLegalHold = d.OnLegalHold,
            CheckedOut = d.CheckedOutByUserId != null,
            CheckedOutByMe = d.CheckedOutByUserId == _currentUserAccessor.UserId,
            CheckedOutByName = d.CheckedOutByName ?? "",
            FileExtension = Path.GetExtension(d.LatestObjectKey ?? ""),
            DocumentType = d.DocumentType ?? "",
            DocumentDate = d.DocumentDate,
            DocumentTime = d.DocumentTime,
            SizeBytes = d.SizeBytes,
            Tags = tagsByDoc.TryGetValue(d.Id, out var tags) ? tags : [],
            CreatedBy = d.CreatedByName ?? "",
            // isPersonalRoot is false by construction: a personal space is a ROOT document, so it is never
            // itself a listed child. Its own resource answers this separately.
            Admits = CreatableChildren.For(rules, d.Id, d.MaskId, isPersonalRoot: false, nameVocabularies),
            // From the rules object already loaded for this page — the mask facts are all in one place, so
            // the icon costs no query.
            Icon = rules.IconOf(d.MaskId),
            SensitivityLabelId = d.SensitivityLabelId,
            SensitivityLabelName = d.SensitivityLabelName ?? "",
            SensitivityLabelColor = d.SensitivityLabelColor,
            VersionCount = d.VersionCount,
            VersionCreatedAt = d.VersionCreatedAt,
            // A listed item advertises its own UNCONDITIONAL sub-resources, not just self+chat (issue #416).
            //
            // Without these a client holding a listing has an id and no addresses, so every sub-resource call
            // it makes has to be composed from a template — which is most of what the ADR 0543 ledger is still
            // counting. Fetching `self` first instead would cost a round trip per row, and paying two calls to
            // follow one rel is the usual reason a codebase abandons hypermedia and goes back to string paths.
            //
            // Rels that depend on the CALLER'S RIGHTS or the item's STATE are deliberately absent —
            // checkout/checkin, external-links, acl-inheritance. Each is a question the full document
            // resource computes once, so those affordances still require the resource itself, and their
            // absence here must NOT be read as "not available" (ADR 0543's rule applies to a resource, not
            // to a summary).
            //
            // That used to be argued as "a listing is the wrong place to answer 'may I?'", and the premise
            // has since changed rather than the conclusion: the objection was COST — a rights resolution
            // per row — and GetCallerRightsForManyAsync now answers a whole page in about the queries one
            // document took. So the three rights a destructive menu needs ARE answered here, as the
            // CanDelete/CanEditIndexData/CanMove flags above (#858). What stays out is the rest, which no
            // menu gates on, and the distinction to hold is between a flag (a yes/no this listing can
            // afford) and a rel (an ADDRESS, which a row should not multiply).
            //
            // A MASK-DERIVED rel is not one of those, and the distinction matters (#564). Whether a folder
            // holds sections and notes is a property of its mask, not of who is asking — and the query is
            // already joining MaskVersions to produce DocumentType, so it costs nothing to answer here.
            // The alternative was for a client to fetch each row's resource when its context menu opens,
            // which is exactly the per-rel round trip ADR 0557 exists to prevent.
            Links = RowLinks(d),
        }).ToList();

        // The per-row rights, in ONE batch for the page (#858) — this is what lets a client gate Delete/Rename/
        // Move honestly instead of offering them to anyone who can see a row and answering with a 403.
        //
        // The mask half of CanCreateChildren is read from `page`, where the mask is in scope — the row DTO does
        // not carry it, and adding it purely to answer this would put a column on the wire for the client to
        // re-derive a rule the server already knows (#854). parentIsPersonalRoot is false by construction: a
        // personal space is a ROOT document, so it is never itself a listed child.
        var maskOf = page.ToDictionary(d => d.Id, d => d.MaskId);

        // Stamped from the SAME rights batch the visibility filter read (ADR 0765) — a second resolution
        // would repeat the page's whole rights walk for an answer already in hand. The mask also says what a
        // folder the core holds may offer at all (ADR 1066).
        Hypermedia.RowCapabilities.Stamp(children, r => r.Id,
            r => ChildCreationPolicy.AdmitsPlainChild(maskOf[r.Id], parentIsPersonalRoot: false), rightsByRow, r => maskOf[r.Id]);
        var encryptedFor = await _encryptedFor.ForRowsAsync(page.Select(d => (d.Id, d.MaskId)), cancellationToken);
        children.ForEach(c => c.EncryptedForCaller = encryptedFor.GetValueOrDefault(c.Id));

        // A password entry met as a document says whether it has a verification code (ADR 1013) or a passkey (ADR 1033): the web never opens
        // one, so this marker is all it can show. One query, and only when the page holds an entry.
        var entries = page.Where(d => d.MaskId == WellKnownMaskIds.PasswordEntry).Select(d => d.Id).ToList();
        var codes = await PasswordStores.PasswordIndexValues.LoadAsync(_dbContext, entries, cancellationToken);
        children.ForEach(c =>
        {
            c.HasCode = codes.GetValueOrDefault(c.Id)?.HasCode ?? false;
            c.HasPasskey = codes.GetValueOrDefault(c.Id)?.HasPasskey ?? false;
        });

        // The FOLDER's own rights, which the per-row batch above does not cover — it answers the children, and
        // this answers their parent. One document's worth of work on a read that already does several queries,
        // and the alternative (a client fetching the folder resource just to learn whether it may upload here)
        // is the per-rel round trip ADR 0557 exists to prevent.
        var folderRights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        return Ok(new DocumentChildrenResource
        {
            Children = children,
            ContentsSortOrder = folderSortOrder,
            CanCreateChildren = folderRights.CanCreateSubItems
                && ChildCreationPolicy.AdmitsPlainChild(folder.MaskId, folder.IsPersonalRoot),
            Links = links,
        });
    }

    /// <summary>The addresses a listed row carries — see the call site for which rels belong here and why.</summary>
    // No longer static: the create affordances now ask the KIND REGISTRY, which a module contributes to, rather
    // than a core-only static table (#1242). Its only caller is an instance action, so nothing else moves.
    private List<Link> RowLinks(DocumentSummaryRow d)
    {
        var links = new List<Link>
        {
            new("self", $"/api/documents/{d.Id}", "GET"),
            new("chat", $"/api/documents/{d.Id}/chat", "GET"),
            new("versions", $"/api/documents/{d.Id}/versions", "GET"),
            new("children", $"/api/documents/{d.Id}/children", "GET"),
            new("mask", $"/api/documents/{d.Id}/mask", "GET"),
            new("index-data", $"/api/documents/{d.Id}/index-data", "GET"),
            new("references", $"/api/documents/{d.Id}/references", "GET"),
            new("referencing-folders", $"/api/documents/{d.Id}/referencing-folders", "GET"),
        };

        // A notebook and a section hold the same two things, so they advertise the same two creates. Everything
        // else advertises neither, and that absence is what tells a client to leave both off its menu.
        if (WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == d.MaskId) is { } rule
            && rule.Admits.Any(a => a.MaskId == WellKnownMaskIds.Note))
        {
            links.Add(new Link("sections", $"/api/documents/{d.Id}/sections", "POST"));
            links.Add(new Link("notes", $"/api/documents/{d.Id}/notes", "POST"));
        }

        // …and the same for the other two typed families (#631). Keyed on the ADMITTED item mask rather than on
        // the folder's, so the rule reads the same way for all three and a new family is a row in the table
        // rather than another branch here.
        if (ChildCreationPolicy.AdmitsTypedItem(d.MaskId, WellKnownMaskIds.Contact))
        {
            links.Add(new Link("contacts", $"/api/documents/{d.Id}/contacts", "POST"));
        }

        // The CREATE question — emitted with POST — so read-only module kinds are excluded and module kinds
        // are seen at all. Off the registry, not a containment predicate over a core-only table (#1242).
        if (_davKinds.AdmitsCalendarEntryCreation(d.MaskId))
        {
            links.Add(new Link("appointments", $"/api/documents/{d.Id}/appointments", "POST"));
        }

        // "New subfolder", gated the same way (#634) — and it must be on the ROW, because that is where both
        // clients' tree nodes get their links (ADR 0555/0557). A rel added only to the single-document GET
        // would leave the menu entry hidden everywhere, since nothing re-fetches a node to populate a menu.
        //
        // parentIsPersonalRoot is false by construction here: a personal space is a ROOT document, so it is
        // never itself a listed child. Its own resource advertises this rel — or withholds it — separately.
        //
        // Unlike the single-document GET this does not check CanCreateSubItems, because a per-row rights
        // resolution is a query per row on the hottest path there is. The mask half of the rule is what this
        // change is for; a caller without the right still meets a 403, exactly as before.

        return links;
    }

    // Batched tag lookup for a page of documents (ADR "List-row columns and sorting") — one query, grouped by
    // document, tags sorted; empty for a document with none.
    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("children")]
    public async Task<IActionResult> HeadChildren(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }


    // Plain mutable classes, not records — same XmlSerializer rationale as elsewhere.
    public class ReferencingFolderResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        // Full folder path, e.g. "Repositories / Contracts / 2026" — disambiguates same-named folders.
        public string Path { get; set; } = string.Empty;
    }

    // The document's real (primary) home folder — where it actually lives, as opposed to the folders that merely
    // reference it (ADR 0506). Null when the item is a repository root, or when the caller can't see the parent.
    public class PrimaryLocationResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    public class ReferencingFoldersResource : HypermediaResource
    {
        public PrimaryLocationResource? PrimaryLocation { get; set; }

        public List<ReferencingFolderResource> Folders { get; set; } = [];
    }

    private record ReferencingFolderRow(Guid ReferenceId, DateTimeOffset CreatedAt, Guid FolderId, string FolderName);

    // Lists every folder that holds a reference (shortcut) to this item — the inverse of
    // DocumentReferencesController's per-folder listing. See ADR "References-of-an-item list". Requires CanSee
    // on the item, and each referencing folder is filtered to those the caller can also see (so it can't leak
    // the existence of a folder the caller has no access to). Cursor-paginated over the DocumentReference row
    // (CreatedAt asc, Id asc), same per-item-filtered walk as RepositoriesController.List.
    [HttpGet("referencing-folders")]
    public async Task<IActionResult> ListReferencingFolders(Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // The document's real home (ADR 0506): its parent folder, shown as the prominent "Primary location" row.
        // Null when the item is a repository root (no parent) or when the caller can't see the parent — don't
        // leak a location the caller has no access to.
        var realParentId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.ParentId)
            .SingleAsync(cancellationToken);

        PrimaryLocationResource? primaryLocation = null;
        if (realParentId is { } parentId && await _access.CanSeeAsync(parentId, cancellationToken))
        {
            var parentName = await _dbContext.Documents
                .Where(d => d.Id == parentId)
                .Select(d => d.Name)
                .SingleAsync(cancellationToken);

            primaryLocation = new PrimaryLocationResource
            {
                Id = parentId,
                Name = parentName,
                Path = await _dbContext.BuildFolderPathAsync(parentId, cancellationToken),
                Links = [new Link("open", $"/api/documents/{parentId}", "GET")],
            };
        }

        var pageSize = PageSize.Resolve(limit);

        // The parent folder must still exist (the join to Documents applies the soft-delete filter).
        var query = _dbContext.DocumentReferences
            .Where(r => r.TargetDocumentId == documentId && _dbContext.Documents.Any(d => d.Id == r.ParentFolderId));

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(r => r.CreatedAt > cursorCreatedAt || (r.CreatedAt == cursorCreatedAt && r.Id > cursorId));
        }

        var candidates = await query
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new ReferencingFolderRow(
                r.Id,
                r.CreatedAt,
                r.ParentFolderId,
                _dbContext.Documents.Where(d => d.Id == r.ParentFolderId).Select(d => d.Name).FirstOrDefault()!))
            .ToListAsync(cancellationToken);

        var visible = new List<ReferencingFolderResource>();
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;
        var hasMore = false;

        foreach (var candidate in candidates)
        {
            if (visible.Count >= pageSize)
            {
                if (await _access.CanSeeAsync(candidate.FolderId, cancellationToken))
                {
                    hasMore = true;
                    break;
                }

                lastCreatedAt = candidate.CreatedAt;
                lastId = candidate.ReferenceId;
                continue;
            }

            if (await _access.CanSeeAsync(candidate.FolderId, cancellationToken))
            {
                visible.Add(new ReferencingFolderResource
                {
                    Id = candidate.FolderId,
                    Name = candidate.FolderName,
                    Path = await _dbContext.BuildFolderPathAsync(candidate.FolderId, cancellationToken),
                    Links = [new Link("open", $"/api/documents/{candidate.FolderId}", "GET")],
                });
            }

            lastCreatedAt = candidate.CreatedAt;
            lastId = candidate.ReferenceId;
        }

        var links = new List<Link> { new("self", Url.Action(nameof(ListReferencingFolders), new { documentId, cursor, limit = pageSize })!, "GET") };

        if (hasMore && lastCreatedAt is { } created && lastId is { } id)
        {
            var nextCursor = Cursor.Encode(created, id);
            links.Add(new Link("next", Url.Action(nameof(ListReferencingFolders), new { documentId, cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new ReferencingFoldersResource { PrimaryLocation = primaryLocation, Folders = visible, Links = links });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead("referencing-folders")]
    public async Task<IActionResult> HeadReferencingFolders(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await _access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        return NoContent();
    }
}
