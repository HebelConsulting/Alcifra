using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.Certificates;
using Alcifra.Api.Hypermedia;

namespace Alcifra.Api.Controllers;

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
public class CertificatesController(CertificateEnrolment enrolment, CertificateRevocation revocation) : ControllerBase
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

        /// <summary>The certificate's subject key identifier: the other way an envelope names it (#2007).</summary>
        public string? SubjectKeyIdentifier { get; set; }

        /// <summary>Who holds it, as the listing's own rights allow (an administrator every holder, a user themself).</summary>
        public string? HolderName { get; set; }

        public string? HolderEmail { get; set; }

        /// <summary>Why it was revoked (#2278): lost, compromised, replaced, left, or the words written for Other.</summary>
        public string? RevocationReason { get; set; }
    }

    /// <summary>A revocation (#2278): one of the reasons, or "other" with a text of its own.</summary>
    public class RevocationRequest
    {
        public string? Reason { get; set; }

        public string? Text { get; set; }
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

        /// <summary>The answered challenge, where the tenant binds by possession and the caller is no administrator (ADR 1061).</summary>
        public PossessionRequest? Possession { get; set; }
    }

    /// <summary>The token a challenge answered with, and the nonce the envelope held, base64.</summary>
    public class PossessionRequest
    {
        public string Token { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
    }

    public class ChallengeRequest
    {
        public string CertificatePem { get; set; } = string.Empty;
    }

    public class ChallengeResource
    {
        /// <summary>The nonce sealed to the certificate: a CMS envelope, base64.</summary>
        public string Envelope { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? holder, CancellationToken cancellationToken)
    {

        // The holder is HONOURED, not merely a switch for "the tenant's view": `certificates list --holder` promised
        // "only this holder's" while the server ignored the value and listed everybody. Whether the caller may ask at
        // all, and what an omitted holder means, are the enrolment's rules — one place, one answer.
        var rows = await enrolment.ListAsync(holder, cancellationToken);
        var mayRevoke = await revocation.WhoseKeysMayBeRevokedAsync(cancellationToken);
        return Ok(rows.Select(row => Resource(row, mayRevoke)));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Enrol([FromBody] EnrolRequest request, CancellationToken cancellationToken)
    {
        var evidence = request.Attestation is { } a
            ? new Infrastructure.Certificates.Attestation.AttestationEvidence(a.Format, a.AttestationCertificatePem, a.IntermediateCertificatePem, a.Slot)
            : null;
        var proof = request.Possession is { } p ? new PossessionProof(p.Token, p.Nonce) : null;
        var (enrolled, created) = await enrolment.EnrolAsync(request.CertificatePem, request.Label, request.Holder, evidence, cancellationToken, proof);

        // An existing enrolment that gained its attestation is not a new resource: 200 with what it now is.
        var mayRevoke = await revocation.WhoseKeysMayBeRevokedAsync(cancellationToken);
        return created ? Created($"/api/documents/{enrolled.DocumentId}", Resource(enrolled, mayRevoke)) : Ok(Resource(enrolled, mayRevoke));
    }

    /// <summary>Under Possession, a member proves they hold the key by opening what this seals to it (ADR 1061). Nothing is
    /// stored, so this changes nothing to audit; the enrolment that follows is audited.</summary>
    [HttpPost("challenge")]
    public async Task<IActionResult> Challenge([FromBody] ChallengeRequest request, CancellationToken cancellationToken)
    {
        var (envelope, token) = await enrolment.ChallengeAsync(request.CertificatePem, cancellationToken);
        return Ok(new ChallengeResource { Envelope = Convert.ToBase64String(envelope), Token = token });
    }

    /// <summary>
    /// Revokes the key (#2278, ADR 1083): its holder or a user administrator, with a reason. A transition of its own,
    /// reached by the listed key's <c>revoke</c> rel, so a holder needs no rights on the Reader certificates folder.
    /// </summary>
    [HttpPost("{documentId:guid}/revocation")]
    public async Task<IActionResult> Revoke(Guid documentId, [FromBody] RevocationRequest request, CancellationToken cancellationToken)
    {
        await revocation.RevokeAsync(documentId, request.Reason, request.Text, cancellationToken);
        return NoContent();
    }

    private static EnrolledResource Resource(CertificateEnrolment.Listed row, Func<Guid, bool> mayRevoke) => new()
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
        SubjectKeyIdentifier = row.SubjectKeyIdentifier,
        HolderName = row.HolderName,
        HolderEmail = row.HolderEmail,
        RevocationReason = row.RevocationReason,
        // A revoked key offers no revoke: the rel says what the caller may do here, now (ADR 0543).
        Links = row.RevokedAt is null && mayRevoke(row.HolderUserId)
            ? [new Link("document", $"/api/documents/{row.DocumentId}", "GET"), new Link("revoke", $"/api/certificates/{row.DocumentId}/revocation", "POST")]
            : [new Link("document", $"/api/documents/{row.DocumentId}", "GET")],
    };
}
