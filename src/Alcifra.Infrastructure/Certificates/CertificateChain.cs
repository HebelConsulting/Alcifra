using System.Security.Cryptography.X509Certificates;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// A filed certificate document's content to its END-ENTITY certificate (#1790): moved from the Encryption Module's
/// projection, whose copy retires with its next release.
/// </summary>
/// <remarks>
/// The leaf is resolved structurally, never by position: a bundle's order is a convention, and picking the issuer
/// would envelope to the certification authority, who could then open what the reader cannot. The leaf is the one
/// certificate that issues no other in the bundle, compared on the distinguished names. This is a question about
/// the file's shape, not a trust decision.
/// </remarks>
public static class CertificateChain
{
    public sealed record Result(X509Certificate2? Leaf, IReadOnlyList<X509Certificate2> All, string? Refusal)
    {
        public bool Ok => Leaf is not null;
    }

    public static Result Read(string pem)
    {
        List<X509Certificate2> certificates;
        try
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPem(pem);
            certificates = [.. collection];
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or ArgumentException or FormatException)
        {
            return new Result(null, [], "The file is not a PEM-encoded certificate.");
        }

        if (certificates.Count == 0)
        {
            return new Result(null, [], "The file contains no certificate.");
        }

        var leaves = certificates
            .Where(candidate => !certificates.Any(other =>
                !ReferenceEquals(other, candidate)
                && string.Equals(other.IssuerName.Name, candidate.SubjectName.Name, StringComparison.Ordinal)))
            .ToList();
        return leaves.Count switch
        {
            1 => new Result(leaves[0], certificates, null),
            0 => new Result(null, certificates,
                "Every certificate in this file issues another, so none of them is the end-entity. That is a loop, not a chain."),
            _ => new Result(null, certificates,
                $"This file holds {leaves.Count} certificates that issue none of the others, so which one addresses the "
                + "reader is ambiguous. File one certificate and its chain, not several."),
        };
    }
}
