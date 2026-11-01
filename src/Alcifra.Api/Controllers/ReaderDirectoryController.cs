using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Certificates;
using Alcifra.Api.Documents;
using Alcifra.Api.Hypermedia;
using Alcifra.Api.Pagination;
using Alcifra.Infrastructure.Certificates.Attestation;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// Who an audience can be sealed to (ADR 0976): the tenant's readers — users holding a USABLE reader certificate — and
/// the decision on a certificate pasted with its attestation chain.
/// </summary>
/// <remarks>
/// <para>
/// Readable by any signed-in user, because choosing the audience of a sealed document is an author's act and names
/// alone cannot be sealed to. What it discloses is deliberately narrow: a display name and the certificates that
/// address that person. A certificate is public data; the only fact revealed is that somebody holds a usable key.
/// No e-mail addresses, no rights, no ACL, and nobody deactivated — they cannot see a document, so sealing to them
/// would be a promise nobody can keep.
/// </para>
/// <para>
/// <b>The certificates are the delivery path's own answer.</b> Each candidate is asked through
/// <see cref="ReaderCertificateSet.ForUserAsync"/> — the module where one answers, else the reader's own column — so
/// the directory can never offer a certificate the server would not then address an envelope to.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/reader-directory")]
[Authorize]
public class ReaderDirectoryController(
    AlcifraDbContext dbContext,
    ReaderCertificateSet readerCertificates,
    EnrolmentAttestation attestation) : ControllerBase
{
    public class ReaderDirectoryResource : HypermediaResource
    {
        public List<ReaderResource> Readers { get; set; } = [];
    }

    public class ReaderResource : HypermediaResource
    {
        public Guid UserId { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public List<ReaderCertificateResource> Certificates { get; set; } = [];
    }

    public class ReaderCertificateResource
    {
        /// <summary>What the holder calls the key ("YubiKey 5C"), else the subject's common name.</summary>
        public string Label { get; set; } = string.Empty;

        public string Subject { get; set; } = string.Empty;

        /// <summary>With <see cref="Serial"/>, how an envelope names its recipient (ADR 0969); null when unreadable.</summary>
        public string? Issuer { get; set; }

        public string Serial { get; set; } = string.Empty;

        /// <summary>SHA-256 of the leaf, uppercase hex — the same identifier the enrolment listing uses.</summary>
        public string Thumbprint { get; set; } = string.Empty;

        public DateTimeOffset NotAfter { get; set; }

        public string CertificatePem { get; set; } = string.Empty;
    }

    public class ValidationRequest
    {
        public string CertificatePem { get; set; } = string.Empty;

        /// <summary>
        /// The key's attestation, as the token reports it (ADR 0963). The SAME wire shape the enrolment door binds —
        /// one DTO, so the two doors cannot drift into accepting different bundles.
        /// </summary>
        public CertificatesController.AttestationRequest? Attestation { get; set; }
    }

    public class ValidationResource : HypermediaResource
    {
        public ReaderCertificateResource Certificate { get; set; } = new();
    }

    /// <summary>One page of readers, ordered by the user's creation (the pagination convention, ADR 0207).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] string? search, CancellationToken cancellationToken)
    {
        var pageSize = PageSize.Resolve(limit);

        // The CANDIDATES the core can see holding a certificate: an enrolment that is not revoked (the user's own column
        // retired, #1970 slice 4). A module that keeps certificates only in its own store is not enumerable here (ADR 0976
        // says so); its readers are reached through the pasted-chain path.
        var query = dbContext.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .Where(u => dbContext.EnrolledCertificates.Any(c => c.UserId == u.Id && c.RevokedAt == null));

        if (search is { Length: > 0 } term)
        {
            var lowered = term.Trim().ToLowerInvariant();
            if (lowered.Length > 0)
            {
                query = query.Where(u => u.DisplayName.ToLower().Contains(lowered));
            }
        }

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(u => u.CreatedAt > cursorCreatedAt || (u.CreatedAt == cursorCreatedAt && u.Id > cursorId));
        }

        var fetched = await query
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .Take(pageSize + 1)
            .Select(u => new { u.Id, u.DisplayName, u.CreatedAt })
            .ToListAsync(cancellationToken);

        var (page, hasMore) = Cursor.Split(fetched, pageSize);
        var labels = await LabelsAsync(page.Select(u => u.Id).ToList(), cancellationToken);

        var readers = new List<ReaderResource>(page.Count);
        foreach (var user in page)
        {
            // THE DELIVERY PATH'S ANSWER, not a second rule (ADR 0976): what the module says this reader is addressed
            // by, else the reader's own column.
            var (certificates, _) = await readerCertificates.ForUserAsync(user.Id, cancellationToken);
            var described = new List<ReaderCertificateResource>(certificates.Count);
            foreach (var pem in certificates)
            {
                if (Describe(pem) is { } certificate)
                {
                    // What the holder calls the key, where the registry knows the thumbprint — else the subject's
                    // common name, which is the honest answer for a certificate enrolled through the column.
                    if (labels.GetValueOrDefault((user.Id, certificate.Thumbprint)) is { Length: > 0 } label)
                    {
                        certificate.Label = label;
                    }

                    described.Add(certificate);
                }
            }

            // A candidate whose module answered "none" is not a reader today; leaving it off the page is the honest
            // answer, and the cursor still moves past it.
            if (described.Count > 0)
            {
                readers.Add(new ReaderResource { UserId = user.Id, DisplayName = user.DisplayName, Certificates = described });
            }
        }

        var resource = new ReaderDirectoryResource { Readers = readers };
        resource.Links.Add(new Link("self",
            Url.Action(nameof(List), new { cursor, limit = pageSize, search })!, "GET"));
        if (hasMore)
        {
            var next = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            resource.Links.Add(new Link("next",
                Url.Action(nameof(List), new { cursor = next, limit = pageSize, search })!, "GET"));
        }

        return Ok(resource);
    }

    /// <summary>The same address with no body — the every-GET-has-a-HEAD convention.</summary>
    [HttpHead]
    public IActionResult Head() => NoContent();

    /// <summary>
    /// Whether a pasted certificate may be sealed to, decided by the code the enrolment door already runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only as its whole attestation chain.</b> The leaf alone proves nothing about where its key lives; the PIV
    /// attestation certificate proves the key was generated on the token, and the chain proves which token family.
    /// <see cref="EnrolmentAttestation"/> applies the tenant's rule exactly as enrolment does: broken evidence and
    /// evidence of another key are refused by name, a root the tenant does not trust is refused unless the tenant
    /// allows hardware without attestation, and evidence is required under that same rule.
    /// </para>
    /// <para>
    /// A noun-shaped sub-resource rather than a <c>/validate</c> verb: nothing is created and nothing is stored — the
    /// answer is a certificate the dialog may seal to.
    /// </para>
    /// </remarks>
    [HttpPost("validation")]
    public async Task<IActionResult> Validate([FromBody] ValidationRequest request, CancellationToken cancellationToken)
    {
        // The external-link rule for a usable recipient, reused so the dialog and every other door agree about what a
        // certificate an envelope can be addressed to is (RSA or EC, readable, PEM).
        RecipientCertificate.Validate(request.CertificatePem);

        using var leaf = X509Certificate2.CreateFromPem(request.CertificatePem);
        var evidence = request.Attestation is { } a
            ? new AttestationEvidence(a.Format, a.AttestationCertificatePem, a.IntermediateCertificatePem, a.Slot)
            : null;
        await attestation.DecideAsync(evidence, leaf, cancellationToken);

        return Ok(new ValidationResource { Certificate = Describe(request.CertificatePem)! });
    }

    private async Task<Dictionary<(Guid UserId, string Thumbprint), string>> LabelsAsync(
        List<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var rows = await dbContext.EnrolledCertificates.AsNoTracking()
            .Where(c => userIds.Contains(c.UserId) && c.RevokedAt == null)
            .Select(c => new { c.UserId, c.Thumbprint, c.Label })
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => r.Label.Length > 0)
            .ToDictionary(r => (r.UserId, r.Thumbprint), r => r.Label);
    }

    /// <summary>Describes a PEM for the wire, or null when it does not parse — a stored certificate can go bad.</summary>
    private static ReaderCertificateResource? Describe(string certificatePem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            return new ReaderCertificateResource
            {
                Label = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                Subject = certificate.Subject,
                Issuer = certificate.Issuer,
                Serial = certificate.SerialNumber.ToUpperInvariant(),
                Thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)),
                NotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                CertificatePem = certificatePem,
            };
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
