using System.Security.Cryptography.X509Certificates;

namespace Alcifra.Infrastructure.Certificates;

/// <summary>
/// What a bundle must satisfy before it is filed as an enrolled certificate (Encryption Module ADR 0002).
/// </summary>
/// <remarks>
/// <b>Enrolment validates BEFORE it files.</b> A document filed first and validated later leaves the archive
/// holding artefacts the module will not serve — a certificate a reader can see in their own space and which
/// silently addresses nothing. So every refusal here happens before anything is written, and each one names
/// what is wrong in terms the person who chose the file can act on.
/// </remarks>
public static class EnrolmentPolicy
{
    /// <summary>The leaf that will be enrolled, or why the bundle is refused.</summary>
    public sealed record Result(X509Certificate2? Leaf, string? Refusal)
    {
        public bool Ok => Leaf is not null;
    }

    /// <summary>Applies the whole policy to a filed bundle.</summary>
    public static Result Check(string pem)
    {
        // A PRIVATE KEY is checked on the TEXT, before anything is parsed, and that is not paranoia about
        // ordering: X509Certificate2Collection.ImportFromPem reads CERTIFICATE blocks and silently IGNORES
        // every other label, so HasPrivateKey is false even for a file that plainly contains one. Asking the
        // parsed certificate would therefore always answer "no private key" — a check that passes by
        // construction, which is worse than no check, because it looks like one.
        if (ContainsPrivateKey(pem))
        {
            return new Result(null,
                "This file contains a PRIVATE KEY. Enrol the certificate only — the public half. A private "
                + "key filed into the archive is readable by everyone who can read the document, and it is "
                + "the one thing that must never leave the card or the keystore that generated it.");
        }

        var chain = CertificateChain.Read(pem);
        if (!chain.Ok)
        {
            return new Result(null, chain.Refusal);
        }

        var leaf = chain.Leaf!;

        if (KeyUsageRefusal(leaf) is { } refusal)
        {
            return new Result(null, refusal);
        }

        return new Result(leaf, null);
    }

    /// <summary>
    /// Whether the leaf's declared key usage permits it to receive an envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encryption Module ADR 0002 says "key encipherment", and taking that literally would refuse every EC certificate.</b>
    /// An RSA recipient gets the content-encryption key WRAPPED to its public key, which is
    /// <c>keyEncipherment</c>; an EC recipient gets it through ECDH key AGREEMENT, which is
    /// <c>keyAgreement</c>. They are different bits for the same job, and a card-issued EC certificate carries
    /// the second. So the rule as implemented is *the key may be used to receive an envelope*, which is the
    /// intent the ADR's RSA-era wording expresses.
    /// </para>
    /// <para>
    /// <b>An ABSENT extension is permission, not a gap.</b> X.509 says key usage is unrestricted unless the
    /// extension appears, so refusing a certificate that declares nothing would reject a perfectly usable one
    /// on the grounds that it made no promises.
    /// </para>
    /// </remarks>
    private static string? KeyUsageRefusal(X509Certificate2 leaf)
    {
        var usage = leaf.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is null)
        {
            return null;
        }

        const X509KeyUsageFlags CanReceiveAnEnvelope =
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyAgreement;

        if ((usage.KeyUsages & CanReceiveAnEnvelope) != 0)
        {
            return null;
        }

        return "This certificate's key usage does not permit it to receive an encrypted document — it "
            + $"declares {usage.KeyUsages}, and enveloping needs Key Encipherment (RSA) or Key Agreement "
            + "(elliptic curve). A signing-only certificate cannot open sealed content, so enrolling it "
            + "would address documents to a key that can never read them.";
    }

    /// <summary>
    /// Whether the PEM text carries a private key under any of its spellings.
    /// </summary>
    /// <remarks>
    /// Matched on the LABEL rather than by trying to parse: the three forms (<c>PRIVATE KEY</c> for PKCS#8,
    /// <c>RSA PRIVATE KEY</c> and <c>EC PRIVATE KEY</c> for the older per-algorithm ones) all end in the same
    /// two words, and an encrypted PKCS#8 block says <c>ENCRYPTED PRIVATE KEY</c> — so one substring covers
    /// every case, including ones a per-algorithm list would miss as new algorithms appear.
    /// </remarks>
    private static bool ContainsPrivateKey(string pem) =>
        pem.Contains("PRIVATE KEY", StringComparison.Ordinal);
}
