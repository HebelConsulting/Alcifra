using Alcifra.Infrastructure.Modules;

namespace Alcifra.Api.Certificates;

/// <summary>
/// The certificates a USER is addressed by (ADR 0842): the Module's set where one answers, else the core's registry (#1970 slice 4),
/// and why the answer is what it is (ADR 0859). Asked for the reader by the strict tier, and for every recipient of an
/// offline archive (ADR 0961), so the two can never disagree about whose keys open what.
/// </summary>
public sealed class ReaderCertificateSet(
    ModuleReaderCertificates moduleCertificates,
    ILogger<ReaderCertificateSet> logger)
{
    /// <summary>The user's usable certificates (PEM) and the outcome of asking.</summary>
    public async Task<(IReadOnlyList<string> Certificates, ReaderCertificateOutcome Outcome)> ForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        // THE MODULE, where one answers, is the ONLY source (ADR 0842). Not a fallback chain and not a union:
        // a union means a certificate REVOKED in the module still opens documents while a copy lingers in the
        // column, which is a revocation that does not revoke.
        //
        // A lapsed licence and a failure to ask have not said "this reader has none", so nothing else is asked on
        // either, and each is remembered, because ADR 0842 requires the refusals to stay distinguishable and they have
        // different fixes (ADR 0859).
        var fromModule = await moduleCertificates.ForAsync(userId, cancellationToken);
        if (fromModule.ModuleSpoke)
        {
            // THE ANSWER THIS TIER TURNS ON, SAID OUT LOUD (#1498). An empty set here withdraws the download
            // and preview rels — correctly, since nothing can be enveloped — and that withdrawal is silent
            // by construction: the reader sees a document with no content affordance and no reason. It cost
            // an evening's guessing, because every layer looked right and nothing anywhere said "the module
            // answered, and it answered none".
            logger.LogDebug(
                "The module answered which certificates user {UserId} is addressed by: {Count}. "
                + "The enveloping rels are {State}.",
                userId, fromModule.Certificates.Count,
                fromModule.Certificates.Count > 0 ? "advertised" : "WITHDRAWN");

            return ([.. fromModule.Certificates.Select(c => c.CertificatePem)], fromModule.Outcome);
        }

        // No module speaks for this tenant, so the core's registry answered (#1970 slice 4): the reader's attested
        // enrolments. The user's own certificate column is no longer a source.
        logger.LogDebug(
            "No module answers for this tenant; the core's registry answered for user {UserId} with {Count} certificate(s).",
            userId, fromModule.Certificates.Count);
        return ([.. fromModule.Certificates.Select(c => c.CertificatePem)], fromModule.Outcome);
    }
}
