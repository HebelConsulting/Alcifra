using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.AttestationRoots;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Certificates;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Certificates.Attestation;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The installation's trusted attestation roots (#1790, ADR 0963): list, upload, rename, switch on and off. A PLATFORM
/// ADMINISTRATOR's surface: the organisation running the installation decides which hardware it trusts, and a tenant may
/// only narrow that. No client UI, like the KEK surface (ADR 0821): <c>alcifra attestation-roots …</c> follows the rel.
/// </summary>
/// <remarks>
/// A root is identified by its certificate's SHA-256; there is no delete, because an enrolment's evidence names a root,
/// and switching it off already removes every certificate attested to it from every reader answer, at once.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/attestation-roots")]
[Authorize]
public class AttestationRootsController(
    SimplArchiveDbContext dbContext,
    ICurrentPlatformAdministratorAccessor platformAdministrator,
    Concurrency.TrustedAttestationRootVerbs roots,
    IAuditRecorder audit,
    ILogger<AttestationRootsController> logger) : ControllerBase
{
    private const string Self = "/api/attestation-roots";

    public class RootsResource : HypermediaResource
    {
        public List<RootResource> Roots { get; set; } = [];
    }

    public class RootResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Thumbprint { get; set; } = string.Empty;

        /// <summary>The shipped catalogue entry this is, or null for an uploaded root.</summary>
        public string? CatalogueKey { get; set; }

        public bool Enabled { get; set; }
        public DateTimeOffset NotAfter { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string CertificatePem { get; set; } = string.Empty;
    }

    public class UploadRequest
    {
        public string CertificatePem { get; set; } = string.Empty;

        /// <summary>What to call it; the certificate's common name when empty.</summary>
        public string? Name { get; set; }

        /// <summary>Whether to trust it at once. Default off, like a catalogue root.</summary>
        public bool Enabled { get; set; }
    }

    public class UpdateRequest
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        RequirePlatformAdministrator();
        var all = await dbContext.TrustedAttestationRoots.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        return Ok(new RootsResource
        {
            Roots = [.. all.Select(Resource)],
            Links = [new Link("self", Self, "GET"), new Link("upload", Self, "POST")],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public IActionResult Head()
    {
        RequirePlatformAdministrator();
        return NoContent();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        RequirePlatformAdministrator();
        var root = await FindAsync(id, cancellationToken);
        roots.EmitETag(Response, root);
        return Ok(Resource(root));
    }

    [HttpHead("{id:guid}")]
    public async Task<IActionResult> HeadOne(Guid id, CancellationToken cancellationToken)
    {
        RequirePlatformAdministrator();
        roots.EmitETag(Response, await FindAsync(id, cancellationToken));
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Upload([FromBody] UploadRequest request, CancellationToken cancellationToken)
    {
        RequirePlatformAdministrator();
        using var certificate = Parse(request.CertificatePem);
        var thumbprint = AttestationCatalogue.Thumbprint(certificate);
        if (await dbContext.TrustedAttestationRoots.AsNoTracking().FirstOrDefaultAsync(r => r.Thumbprint == thumbprint, cancellationToken) is { } present)
        {
            throw new AttestationRootAlreadyPresentException(present.Name);
        }

        var root = new TrustedAttestationRoot
        {
            Id = Guid.NewGuid(),
            Name = request.Name is { Length: > 0 } name ? name.Trim() : certificate.GetNameInfo(X509NameType.SimpleName, false),
            CertificatePem = certificate.ExportCertificatePem(),
            Thumbprint = thumbprint,
            Enabled = request.Enabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.TrustedAttestationRoots.Add(root);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Evidence stored while this root was unknown chains to it now.
        await AttestationReverification.RunAsync(dbContext, logger, cancellationToken);
        await RecordEverywhereAsync(AuditActions.AttestationRootAdded, root, $"thumbprint {thumbprint}, {(root.Enabled ? "trusted" : "switched off")}", cancellationToken);
        logger.LogInformation("Attestation root {Thumbprint} added ({State})", thumbprint, root.Enabled ? "trusted" : "switched off");

        roots.EmitETag(Response, root);
        return Created($"{Self}/{root.Id}", Resource(root));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRequest request, CancellationToken cancellationToken)
    {
        RequirePlatformAdministrator();
        var root = await FindAsync(id, cancellationToken);
        Concurrency.ConcurrencyHeaders.RequireIfMatch(Request);
        var before = $"'{root.Name}', {(root.Enabled ? "trusted" : "switched off")}";
        await roots.MutateAsync(Request, root,
            apply: () =>
            {
                root.Name = request.Name is { Length: > 0 } name ? name.Trim() : root.Name;
                root.Enabled = request.Enabled;
                return Task.CompletedTask;
            },
            afterCommit: () => RecordEverywhereAsync(AuditActions.AttestationRootChanged, root,
                $"{before} → '{root.Name}', {(root.Enabled ? "trusted" : "switched off")}", cancellationToken),
            cancellationToken: cancellationToken);

        roots.EmitETag(Response, root);
        return Ok(Resource(root));
    }

    private void RequirePlatformAdministrator()
    {
        if (platformAdministrator.PlatformAdministratorId is null)
        {
            throw new AttestationRootsPlatformOnlyException();
        }
    }

    private async Task<TrustedAttestationRoot> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.TrustedAttestationRoots.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new AttestationRootNotFoundException(id);

    // One public CA certificate: no private key, readable, and (where it says) a CA.
    private static X509Certificate2 Parse(string pem)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new AttestationRootInvalidException("it contains a PRIVATE KEY. Upload the public root certificate only.");
        }

        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (CryptographicException)
        {
            throw new AttestationRootInvalidException("it is not a PEM certificate.");
        }

        if (collection.Count != 1)
        {
            throw new AttestationRootInvalidException($"it carries {collection.Count} certificates; upload exactly one root.");
        }

        var certificate = collection[0];
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: false })
        {
            certificate.Dispose();
            throw new AttestationRootInvalidException("it is not a certification authority's certificate.");
        }

        return certificate;
    }

    // The roots are the installation's and the trail is a tenant's (ADR 0315), so the act is recorded in every active tenant,
    // whose readers it concerns, as the KEK acts are (ADR 0821).
    private async Task RecordEverywhereAsync(string action, TrustedAttestationRoot root, string details, CancellationToken cancellationToken)
    {
        var administratorId = platformAdministrator.PlatformAdministratorId!.Value;
        foreach (var tenantId in await dbContext.Tenants.IgnoreQueryFilters(["TenantFilter"]).Where(t => t.Status == TenantStatus.Active)
                     .Select(t => t.Id).ToListAsync(cancellationToken))
        {
            await audit.RecordForActorAsync(Domain.Audit.AuditActorType.PlatformAdministrator, administratorId, "Platform administrator",
                tenantId, action, "AttestationRoot", root.Id, root.Name, details, cancellationToken);
        }
    }

    private static RootResource Resource(TrustedAttestationRoot root)
    {
        using var certificate = X509Certificate2.CreateFromPem(root.CertificatePem);
        return new RootResource
        {
            Id = root.Id,
            Name = root.Name,
            Subject = certificate.Subject,
            Thumbprint = root.Thumbprint,
            CatalogueKey = root.CatalogueKey,
            Enabled = root.Enabled,
            NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            CreatedAt = root.CreatedAt,
            CertificatePem = root.CertificatePem,
            Links = [new Link("self", $"{Self}/{root.Id}", "GET")],
        };
    }
}
