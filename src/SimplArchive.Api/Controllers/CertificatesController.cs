using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Certificates;
using SimplArchive.Api.Hypermedia;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Reader-certificate enrolment (ADR 0936, moved from the Encryption Module): list your certificates, or every one for
/// an administrator who asks with <c>?holder=</c>; enrol one, for yourself where self-service is on, or for somebody
/// else as an administrator. Exists only while an active module licenses enrolment.
/// </summary>
/// <remarks>
/// The shapes are the module's, unchanged, so a client written against <c>encryption:certificates</c> reads them as
/// before; the root advertises the old rel name as an alias of <c>certificates</c> for one release.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/certificates")]
[Authorize]
public class CertificatesController(CertificateEnrolment enrolment) : ControllerBase
{
    public class EnrolledResource : HypermediaResource
    {
        public Guid DocumentId { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Thumbprint { get; set; } = string.Empty;
        public string Serial { get; set; } = string.Empty;
        public DateTimeOffset NotAfter { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? FailsPolicy { get; set; }
        public bool Usable { get; set; }
    }

    public class EnrolRequest
    {
        public string CertificatePem { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? Holder { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? holder, CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);

        // Asking for somebody else's certificates is an administrator's question; anyone else gets an empty answer
        // rather than a refusal, as the module answered.
        var everyone = holder is { Length: > 0 };
        if (everyone && !await enrolment.CallerAdministersAsync(cancellationToken))
        {
            return Ok(Array.Empty<EnrolledResource>());
        }

        var rows = await enrolment.ListAsync(everyone, cancellationToken);
        return Ok(rows.Select(Resource));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Enrol([FromBody] EnrolRequest request, CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);
        var enrolled = await enrolment.EnrolAsync(request.CertificatePem, request.Label, request.Holder, cancellationToken);
        return Created($"/api/documents/{enrolled.DocumentId}", Resource(enrolled));
    }

    private static EnrolledResource Resource(CertificateEnrolment.Listed row) => new()
    {
        DocumentId = row.DocumentId,
        Label = row.Label,
        Thumbprint = row.Thumbprint,
        Serial = row.Serial,
        NotAfter = row.NotAfter,
        RevokedAt = row.RevokedAt,
        FailsPolicy = row.FailsPolicy,
        Usable = row.Usable,
        Links = [new Link("document", $"/api/documents/{row.DocumentId}", "GET")],
    };
}
