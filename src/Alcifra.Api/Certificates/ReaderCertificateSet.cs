using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Certificates;

/// <summary>
/// The certificates a USER is addressed by (ADR 0842): the Module's set where one answers, else the user's own column,
/// and why the answer is what it is (ADR 0859). Asked for the reader by the strict tier, and for every recipient of an
/// offline archive (ADR 0961), so the two can never disagree about whose keys open what.
/// </summary>
public sealed class ReaderCertificateSet(
    AlcifraDbContext dbContext,
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
        // Every outcome but NoModule closes the core's own sources — a lapsed licence and a failure to ask
        // have not said "this reader has none" — and each is remembered, because ADR 0842 requires the
        // refusals to stay distinguishable and they have different fixes (ADR 0859).
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

        // Not "no certificate": the module did not speak, and the three ways that happens have three
        // different fixes (ADR 0859). Named here because the core's own sources are about to be tried and
        // whichever answer they give will look like the module's.
        logger.LogDebug(
            "No module answered for user {UserId} ({Outcome}); falling back to the core's own sources.",
            userId, fromModule.Outcome);

        var own = await dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.SmimeCertificatePem)
            .FirstOrDefaultAsync(cancellationToken);

        // The reader's own column, and nothing after it. The encryption service's registry used to be asked next
        // and is RETIRED (ADR 0890, SimplArchiveEncryptionService#23): it enveloped to whatever PEM was last
        // registered against an e-mail, with no history and no revocation, so a re-issued card was silently never
        // offered. A tenant whose mode envelopes cannot start without a module answering (ADR 0890), so on such a
        // tenant this point is reached only for a licence lapsed or a module failing to answer.
        return (Usable(own) is { } usable ? [usable] : [], fromModule.Outcome);
    }

    /// <summary>The certificate if it parses, else null — the same judgement for both sources.</summary>
    private static string? Usable(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            RecipientCertificate.Validate(pem);
            return pem;
        }
        catch (Errors.Exceptions.ExternalLinks.InvalidRecipientCertificateException)
        {
            // Present but unusable. The rel disappears and the reader is told to register a current one, which
            // is a better answer than a button that fails.
            return null;
        }
    }
}
