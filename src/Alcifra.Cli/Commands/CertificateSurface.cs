using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Where certificate enrolment lives (the core's since ADR 0936) — resolved by following a rel, once.
/// </summary>
/// <remarks>
/// <para>
/// ONE place, because four commands need the same address and four copies of "follow this rel" is four
/// places for the rel name to drift from the module's declaration.
/// </para>
/// <para>
/// <b>A missing rel is a real answer here</b> (ADR 0543): the API root emits these rels only for a
/// tenant where a module licensing enrolment is active, so absence means no such module is — which is a
/// different problem from anything in the certificate the caller is holding, and the refusal says so.
/// </para>
/// </remarks>
internal static class CertificateSurface
{
    // The core's rel since enrolment moved into the core (ADR 0936); the Encryption Module's own name is followed when an
    // installation still runs a release that only advertises that, until the release after next.
    internal const string Rel = "certificates";

    internal const string LegacyRel = "encryption:certificates";

    internal static async Task<string> AddressAsync(Hypermedia hypermedia, CancellationToken cancellationToken)
    {
        foreach (var rel in new[] { Rel, LegacyRel })
        {
            try
            {
                return await hypermedia.RootHrefAsync(rel, cancellationToken);
            }
            catch (CliException)
            {
                // not advertised under this name; try the next
            }
        }

        throw new CliException(
            "This installation does not offer certificate enrolment for this tenant: no active module licenses it. "
            + "File the Encryption Module's licence first: alcifra module activate.");
    }
}
