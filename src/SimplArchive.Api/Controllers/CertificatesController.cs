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

        /// <summary>The certificate's issuer: with <see cref="Serial"/>, how a sealed envelope names it (ADR 0969).</summary>
        public string? Issuer { get; set; }

        public DateTimeOffset NotAfter { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? FailsPolicy { get; set; }
        public bool Usable { get; set; }

        /// <summary>"attested", "rootNotTrusted" or "notAttested" (ADR 0963): what the key's attestation is worth here.</summary>
        public string Attestation { get; set; } = "notAttested";

        /// <summary>The name of the root the key is attested to, where it is.</summary>
        public string? AttestationRoot { get; set; }

        /// <summary>The token's serial number, as its attestation states it.</summary>
        public string? DeviceSerial { get; set; }
    }

    /// <summary>A key's attestation as the enrolling client read it from the token (ADR 0963).</summary>
    public class AttestationRequest
    {
        /// <summary>The attestation format; "piv" is verified today.</summary>
        public string Format { get; set; } = string.Empty;

        /// <summary>The token's certificate for the key slot, PEM.</summary>
        public string AttestationCertificatePem { get; set; } = string.Empty;

        /// <summary>The token's attestation certificate that signed it, then any further intermediates, PEM, concatenated.</summary>
        public string IntermediateCertificatePem { get; set; } = string.Empty;

        /// <summary>The key slot it was read from ("9d").</summary>
        public string? Slot { get; set; }
    }

    public class EnrolRequest
    {
        public string CertificatePem { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? Holder { get; set; }

        /// <summary>The key's attestation; required unless the tenant allows hardware without attestation (ADR 0963).</summary>
        public AttestationRequest? Attestation { get; set; }
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
        var evidence = request.Attestation is { } a
            ? new Infrastructure.Certificates.Attestation.AttestationEvidence(a.Format, a.AttestationCertificatePem, a.IntermediateCertificatePem, a.Slot)
            : null;
        var (enrolled, created) = await enrolment.EnrolAsync(request.CertificatePem, request.Label, request.Holder, evidence, cancellationToken);

        // An existing enrolment that gained its attestation is not a new resource: 200 with what it now is.
        return created ? Created($"/api/documents/{enrolled.DocumentId}", Resource(enrolled)) : Ok(Resource(enrolled));
    }

    private static EnrolledResource Resource(CertificateEnrolment.Listed row) => new()
    {
        DocumentId = row.DocumentId,
        Label = row.Label,
        Thumbprint = row.Thumbprint,
        Serial = row.Serial,
        Issuer = row.Issuer,
        NotAfter = row.NotAfter,
        RevokedAt = row.RevokedAt,
        FailsPolicy = row.FailsPolicy,
        Usable = row.Usable,
        Attestation = row.Attestation,
        AttestationRoot = row.AttestationRoot,
        DeviceSerial = row.DeviceSerial,
        Links = [new Link("document", $"/api/documents/{row.DocumentId}", "GET")],
    };
}
