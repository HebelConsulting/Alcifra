using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.Certificates;
using Alcifra.Infrastructure.Certificates.Policy;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The tenant's certificate policy (ADR 0936, moved from the Encryption Module): what a certificate must meet before it
/// is enrolled. Anyone may read it, because a refused enrollee needs to see what is wanted; only an administrator
/// replaces it, and a policy that cannot work is refused with every problem at once.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/certificate-policy")]
[Authorize]
public class CertificatePolicyController(CertificateEnrolment enrolment) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CertificatePolicyResource>> Get(CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);
        var (policy, tag) = await enrolment.CurrentPolicyWithTagAsync(cancellationToken);
        Response.Headers.ETag = $"\"{tag}\"";
        return Ok(Resource(policy));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);
        Response.Headers.ETag = $"\"{(await enrolment.CurrentPolicyWithTagAsync(cancellationToken)).Tag}\"";
        return NoContent();
    }

    // The precondition is the tag of the policy the administrator SAW (ADR 0794): 428 without one, 412 when another
    // save came first.
    [HttpPut]
    public async Task<ActionResult<CertificatePolicyResource>> Put(
        [FromBody] CertificatePolicyResource request, CancellationToken cancellationToken)
    {
        await enrolment.EnsureOfferedAsync(cancellationToken);
        // Either signal for leaving Strict wins (CertificatePolicy.EffectiveBinding): an older client switches the address
        // rule off while echoing the binding it read. Both are stored consistently.
        var binding = Binding(request.Binding) == CertificateBinding.Possession || !request.SanMustMatchAccount
            ? CertificateBinding.Possession
            : CertificateBinding.Strict;
        var policy = new CertificatePolicy
        {
            IssuerDnPattern = Trimmed(request.IssuerDnPattern),
            SubjectCnPattern = Trimmed(request.SubjectCnPattern),
            Binding = binding,
            SanMustMatchAccount = binding == CertificateBinding.Strict,
            MinimumKeySizeBits = request.MinimumKeySizeBits,
            PermittedCurves = request.PermittedCurves ?? [],
            RequiredExtendedKeyUsages = [.. (request.RequiredExtendedKeyUsages ?? []).Where(o => o.Length > 0)],
            TrustAnchorPem = Trimmed(request.TrustAnchorPem),
            Rules = [.. (request.Rules ?? []).Select(r => new CertificateRule(r.Element ?? string.Empty, r.Pattern ?? string.Empty))],
        };
        var tag = await enrolment.SavePolicyAsync(policy, Concurrency.ConcurrencyHeaders.RequireIfMatch(Request), cancellationToken);
        Response.Headers.ETag = $"\"{tag}\"";
        return Ok(Resource(policy));
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CertificateBinding? Binding(string? value) => Trimmed(value)?.ToLowerInvariant() switch
    {
        null => null,
        "strict" => CertificateBinding.Strict,
        "possession" => CertificateBinding.Possession,
        _ => throw new Errors.Exceptions.Certificates.CertificateBindingUnknownException(value!.Trim()),
    };

    private static CertificatePolicyResource Resource(CertificatePolicy policy) => new()
    {
        IssuerDnPattern = policy.IssuerDnPattern,
        SubjectCnPattern = policy.SubjectCnPattern,
        Binding = policy.EffectiveBinding.ToString().ToLowerInvariant(),
        SanMustMatchAccount = policy.EffectiveBinding == CertificateBinding.Strict,
        MinimumKeySizeBits = policy.MinimumKeySizeBits,
        PermittedCurves = [.. policy.PermittedCurves],
        DefaultMinimumRsaKeySizeBits = CertificatePolicy.DefaultMinimumRsaKeySizeBits,
        DefaultPermittedCurves = [.. CertificatePolicy.DefaultPermittedCurves],
        RequiredExtendedKeyUsages = [.. policy.RequiredExtendedKeyUsages],
        TrustAnchorPem = policy.TrustAnchorPem,
        Rules = [.. policy.Rules.Select(r => new CertificateRuleResource { Element = r.Element, Pattern = r.Pattern })],
        // Served WITH the policy, so the form that edits a rule already holds what a rule may address (ADR 0557).
        AddressableElements = CertificateElement.Describe(),
    };

    public class CertificatePolicyResource
    {
        public string? IssuerDnPattern { get; set; }
        public string? SubjectCnPattern { get; set; }
        // On unless the request says otherwise (ADR 0971): a replacement that leaves the field out keeps the product's
        // default instead of silently opting the tenant out of it.
        public bool SanMustMatchAccount { get; set; } = true;

        /// <summary><c>strict</c> or <c>possession</c> (ADR 1017). Left out, the address rule above decides, as before.</summary>
        public string? Binding { get; set; }
        public int? MinimumKeySizeBits { get; set; }
        public List<string>? PermittedCurves { get; set; }
        public int DefaultMinimumRsaKeySizeBits { get; set; }
        public List<string>? DefaultPermittedCurves { get; set; }
        public List<string>? RequiredExtendedKeyUsages { get; set; }
        public string? TrustAnchorPem { get; set; }
        public List<CertificateRuleResource>? Rules { get; set; }
        public string? AddressableElements { get; set; }
    }

    public class CertificateRuleResource
    {
        public string? Element { get; set; }
        public string? Pattern { get; set; }
    }
}
