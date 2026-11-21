using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.Pagination;
using Alcifra.Api.Provisioning;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// Implements ADR "Tenant onboarding and platform-admin mechanism", redesigned by ADR "Document-scope
/// authorization retrofit for User, and tenant-administrator-driven onboarding" — the only Api path to
/// create a Tenant; previously this only ever happened via direct DB seeding. Every action requires the
/// caller to be a genuine, authenticated PlatformAdministrator — a valid ServiceAccount token isn't
/// enough, even though it's a valid token, since it isn't a platform-admin one.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tenants")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly AlcifraDbContext _dbContext;
    private readonly ICurrentPlatformAdministratorAccessor _currentPlatformAdministratorAccessor;
    private readonly ITenantProvisioningService _tenantProvisioningService;

    private readonly IAuditRecorder _audit;
    private readonly ITenantBucketMigration _bucketMigration;
    private readonly Alcifra.Application.Abstractions.IModuleMaskDeclarations _maskDeclarations;

    public TenantsController(
        AlcifraDbContext dbContext,
        ICurrentPlatformAdministratorAccessor currentPlatformAdministratorAccessor,
        ITenantProvisioningService tenantProvisioningService,
        IAuditRecorder audit,
        ITenantBucketMigration bucketMigration,
        Alcifra.Application.Abstractions.IModuleMaskDeclarations maskDeclarations)
    {
        _maskDeclarations = maskDeclarations;
        _dbContext = dbContext;
        _currentPlatformAdministratorAccessor = currentPlatformAdministratorAccessor;
        _tenantProvisioningService = tenantProvisioningService;
        _audit = audit;
        _bucketMigration = bucketMigration;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class TenantResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>What one batch of a tenant's bucket migration did (ADR 1000); <c>next</c> continues it.</summary>
    public class StorageMigrationBatchResource : HypermediaResource
    {
        public int Copied { get; set; }

        public int AlreadyPresent { get; set; }

        public int Removed { get; set; }

        public int Retained { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? RetainedUntil { get; set; }
    }

    public class TenantsListResource : HypermediaResource
    {
        public List<TenantResource> Tenants { get; set; } = [];
    }

    public class CreateTenantRequest
    {
        public string Name { get; set; } = string.Empty;

        public string AdministratorEmail { get; set; } = string.Empty;

        public string AdministratorDisplayName { get; set; } = string.Empty;

        // Defaults to the tenant's own Name if omitted.
        public string? RepositoryName { get; set; }

        /// <summary>
        /// The initial administrator password, where the CALLER must know it up front — a deployment seeding a
        /// showcase tenant at reset time. Omitted, a random one is generated and returned once. A supplied one is
        /// held to <c>PasswordPolicy</c> like any password a person chooses.
        /// </summary>
        public string? AdministratorPassword { get; set; }
    }

    public class TenantAdministratorResource
    {
        public Guid Id { get; set; }

        public string Email { get; set; } = string.Empty;

        // Present only here — never retrievable again, only ever stored hashed (PasswordHasher<User>).
        public string Password { get; set; } = string.Empty;
    }

    public class CreatedRepositoryResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class CreateTenantResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public TenantAdministratorResource TenantAdministrator { get; set; } = new();

        public CreatedRepositoryResource Repository { get; set; } = new();
    }

    // Creates the Tenant, seeds its 3 well-known masks, and provisions a TenantAdministrator User
    // (IsTenantAdmin = true, plus CanManageRepositories/CanManageMasks/CanManageServiceAccounts) who
    // creates the tenant's first repository itself — a real principal AclEntry/Document.CreatedByUserId
    // already fully support, unlike PlatformAdministrator, which isn't a valid AclEntry principal at all.
    // This is the one place in the whole Api where granting full rights with no escalation cap is sound: a
    // PlatformAdministrator delegates trust into a brand-new tenant it's creating, unlike
    // AclEntry/ServiceAccount/User creation, which cap what a caller can hand out from its own existing
    // rights within an already-existing tenant. See ADR "Document-scope authorization retrofit for User,
    // and tenant-administrator-driven onboarding" (supersedes the original bootstrap-ServiceAccount shape
    // from ADR "Tenant onboarding and platform-admin mechanism").
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        // A supplied password is for the caller that must know it up front (a reset-time seed); omitted, a random
        // one is generated and returned once below. The provisioning logic itself lives in the shared service, so
        // this and the demo seeder cannot drift.
        var provisioned = await _tenantProvisioningService.ProvisionAsync(
            request.Name,
            request.AdministratorEmail,
            request.AdministratorDisplayName,
            request.RepositoryName,
            administratorPassword: request.AdministratorPassword,
            cancellationToken);

        // Alcifra's own manual, filed into the new tenant's standard repository at its founding (ADR 0891) — after
        // the provisioning commit, in a scope of its own whose tenant is the new one. A refusal is logged, never thrown.
        await Alcifra.Api.Manuals.ManualStartupFiling.FileForTenantAsync(
            HttpContext.RequestServices, provisioned.TenantId, [],
            HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Alcifra.Api.Manuals.ManualFiler"),
            cancellationToken);

        var resource = new CreateTenantResource
        {
            Id = provisioned.TenantId,
            Name = provisioned.TenantName,
            TenantAdministrator = new TenantAdministratorResource
            {
                Id = provisioned.AdministratorId,
                Email = provisioned.AdministratorEmail,
                Password = provisioned.AdministratorPassword,
            },
            Repository = new CreatedRepositoryResource
            {
                Id = provisioned.RepositoryId,
                Name = provisioned.RepositoryName,
            },
            Links = [new Link("self", $"/api/tenants/{provisioned.TenantId}", "GET")],
        };

        // The actor is a PlatformAdministrator with no current tenant; the event belongs to the
        // just-created tenant, so its id is passed explicitly rather than read from the accessor.
        await _audit.RecordAsync(
            AuditActions.TenantCreated,
            "Tenant",
            provisioned.TenantId,
            provisioned.TenantName,
            tenantId: provisioned.TenantId,
            cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(Get), new { tenantId = provisioned.TenantId }, resource);
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.Tenants.AsQueryable();

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(t => t.CreatedAt > cursorCreatedAt || (t.CreatedAt == cursorCreatedAt && t.Id > cursorId));
        }

        var fetched = await query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new TenantsListResource
        {
            Tenants = page.Select(BuildResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public IActionResult HeadList()
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{tenantId:guid}")]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(tenant));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{tenantId:guid}")]
    public async Task<IActionResult> Head(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var exists = await _dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    /// <summary>
    /// Moves the next batch of the tenant's objects from its legacy bucket to its current one (ADR 1000): copies with
    /// metadata, retention date and legal hold, then removes each legacy copy no lock keeps.
    /// </summary>
    /// <remarks>
    /// Idempotent, so it needs no <c>If-Match</c>: it changes no resource anybody read, and a repeated or concurrent
    /// call copies nothing twice. <c>alcifra storage migrate</c> follows <c>next</c> until it is absent and reports each
    /// batch as it goes (ADR 0999).
    /// </remarks>
    [HttpPost("{tenantId:guid}/storage-migration")]
    public async Task<IActionResult> MigrateStorage(Guid tenantId, [FromQuery] string? after, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        if (!_bucketMigration.IsConfigured)
        {
            throw new Alcifra.Api.Errors.Exceptions.Storage.BucketMigrationNotConfiguredException();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        var pageSize = PageSize.Resolve(limit);
        var batch = await _bucketMigration.MigrateBatchAsync(tenantId, after, pageSize, cancellationToken);

        if (batch.Copied + batch.Removed > 0)
        {
            await _audit.RecordAsync(
                AuditActions.TenantStorageMigrated,
                "Tenant",
                tenantId,
                tenant.Name,
                details: $"copied {batch.Copied}, removed {batch.Removed}, kept by a lock {batch.Retained}",
                tenantId: tenantId,
                cancellationToken: cancellationToken);
        }

        var links = new List<Link>();
        if (batch.Next is { } next)
        {
            links.Add(new Link("next", Url.Action(nameof(MigrateStorage), new { tenantId, after = next, limit = pageSize })!, "POST"));
        }

        return Ok(new StorageMigrationBatchResource
        {
            Copied = batch.Copied,
            AlreadyPresent = batch.AlreadyPresent,
            Removed = batch.Removed,
            Retained = batch.Retained,
            RetainedUntil = batch.RetainedUntil,
            Links = links,
        });
    }

    /// <summary>One content segment's count for the storage check (#2304): how many versions of its masks live inside it — and how many do not.</summary>
    public class StorageSegmentResource
    {
        public string Segment { get; set; } = string.Empty;

        public int Versions { get; set; }

        public int InSegment { get; set; }

        public int OutOfSegment { get; set; }
    }

    public class StorageSegmentsResource : HypermediaResource
    {
        public List<StorageSegmentResource> Segments { get; set; } = [];
    }

    /// <summary>
    /// The read-only out-of-segment count behind <c>alcifra storage check</c> (#2304): every content segment the loaded
    /// modules declare, and how many versions of its masks are stored somewhere else — the archive's year folders, from
    /// a writer of the years before the one key factory, or from a mask set after filing before the move existed.
    /// </summary>
    /// <remarks>
    /// A platform administrator's question, answered across tenants the same way the bucket migration is: the queries
    /// bypass the tenant filter and name the tenant explicitly, because no tenant is ambient on this route.
    /// </remarks>
    [HttpGet("{tenantId:guid}/storage-out-of-segment")]
    public async Task<IActionResult> StorageOutOfSegment(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        if (!await _dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            return NotFound();
        }

        var segments = new List<StorageSegmentResource>();
        foreach (var segment in _maskDeclarations.ContentSegments.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var maskIds = _maskDeclarations.ContentSegments.Where(p => p.Value == segment).Select(p => p.Key).ToList();
            var maskVersionIds = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
                .Where(v => v.TenantId == tenantId && maskIds.Contains(v.MaskId))
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);

            var prefix = Alcifra.Application.Abstractions.ObjectKeyPrefixes.ModuleContent(tenantId, segment);
            // The filter bypass is applied OUTSIDE the expression tree: a collection expression inside it does not
            // translate (CS9175), and the version query needs the documents query as its subquery.
            var documents = _dbContext.Documents.IgnoreQueryFilters(["TenantFilter"]);
            var versions = _dbContext.DocumentVersions.IgnoreQueryFilters(["TenantFilter"])
                .Where(v => v.TenantId == tenantId
                    && documents.Any(d => d.Id == v.DocumentId && d.TenantId == tenantId
                        && maskVersionIds.Contains(d.MaskVersionId)));

            var inSegment = await versions.CountAsync(v => v.ObjectKey.StartsWith(prefix), cancellationToken);
            var outOfSegment = await versions.CountAsync(v => !v.ObjectKey.StartsWith(prefix), cancellationToken);
            segments.Add(new StorageSegmentResource
            {
                Segment = segment,
                Versions = inSegment + outOfSegment,
                InSegment = inSegment,
                OutOfSegment = outOfSegment,
            });
        }

        return Ok(new StorageSegmentsResource { Segments = segments });
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead("{tenantId:guid}/storage-out-of-segment")]
    public async Task<IActionResult> HeadStorageOutOfSegment(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return await _dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken) ? NoContent() : NotFound();
    }

    private TenantResource BuildResource(Tenant tenant)
    {
        var links = new List<Link> { new("self", $"/api/tenants/{tenant.Id}", "GET") };

        // Only while a migration is configured (ADR 1000): a missing rel tells the CLI there is nothing to move.
        if (_bucketMigration.IsConfigured)
        {
            links.Add(new Link("storage-migration", $"/api/tenants/{tenant.Id}/storage-migration", "POST"));
        }

        // Always advertised, read-only: the storage check (#2304) answers which segmented masks' content sits outside
        // its segment, and an empty answer is a clean bill of health, not an absent door.
        links.Add(new Link("storage-out-of-segment", $"/api/tenants/{tenant.Id}/storage-out-of-segment", "GET"));

        // The tenant's runtime brand (ADR 1103): assigning it is a platform administrator's act, so the rel rides on
        // this collection rather than on the tenant-administrator settings — `alcifra tenant brand` follows it, and
        // never composes the address (ADR 0543/0871).
        links.Add(new Link("brand", $"/api/tenants/{tenant.Id}/brand", "GET"));

        return new TenantResource
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Links = links,
        };
    }
}
