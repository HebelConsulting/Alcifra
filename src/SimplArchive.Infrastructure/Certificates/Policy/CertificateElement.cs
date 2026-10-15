using System.Security.Cryptography.X509Certificates;

namespace SimplArchive.Infrastructure.Certificates.Policy;

/// <summary>
/// Which part of a certificate a generic rule addresses (Encryption Module ADR 0006).
/// </summary>
/// <remarks>
/// <para>
/// <b>The addressing scheme is ENUMERABLE, and that is the whole reason the policy has a validating surface
/// of its own.</b> The requirement asked for rules over <i>"any other element"</i>, and a mechanism that
/// accepted any string would let an administrator save a rule naming something nothing implements — matching
/// nothing, silently, and on a sealed-delivery tenant that locks every reader out of their own documents.
/// So a rule names one of the addresses below or it is refused where it is typed.
/// </para>
/// <para>
/// <b>Two shapes, not one.</b> A distinguished-name component is addressed by OID within either name
/// (<c>subject:2.5.4.10</c> is the subject's Organisation), while a subject-alternative name is addressed by
/// kind (<c>san:dns</c>). Collapsing them into "an element" was tempting and wrong: an OID means nothing to a
/// SAN, and a kind means nothing to a DN, so one namespace would admit halves of addresses that cannot
/// resolve.
/// </para>
/// </remarks>
public static class CertificateElement
{
    /// <summary>The SAN kinds a rule may address.</summary>
    /// <summary>
    /// The SAN kinds a rule may address — <c>dns</c> and <c>email</c>, and deliberately not <c>uri</c>.
    /// </summary>
    /// <remarks>
    /// Bounded by what the framework will parse, not by what X.509 permits.
    /// <c>X509SubjectAlternativeNameExtension</c> enumerates DNS names and IP addresses and nothing else, so
    /// an e-mail SAN is read the way this codebase already reads one — <c>GetNameInfo(EmailName)</c>, which
    /// is what the enveloper uses to address a recipient — and a URI SAN would need hand-rolled ASN.1 over
    /// the extension's raw data.
    /// <para>
    /// Left out rather than hand-rolled: nobody has asked for a rule over a URI SAN, and a parser written
    /// speculatively against a structure this product does not otherwise read is a defect waiting for its
    /// first real certificate. It is one entry in this array plus an extractor when someone does ask.
    /// </para>
    /// </remarks>
    private static readonly string[] SanKinds = ["dns", "email"];

    /// <summary>The whole-certificate addresses — no OID, no kind.</summary>
    private static readonly string[] Whole = ["serial", "thumbprint", "subject", "issuer"];

    /// <summary>
    /// Whether <paramref name="element"/> is an address this module can resolve.
    /// </summary>
    /// <remarks>
    /// The OID is not checked against a registry: an organisation's own CA may put anything in its DN, and
    /// refusing an OID nobody has heard of would refuse exactly the customer this feature is for. What IS
    /// checked is that it LOOKS like an OID — dotted digits — because a typo there would otherwise become a
    /// rule that matches nothing.
    /// </remarks>
    public static bool IsKnown(string element) =>
        Whole.Contains(element, StringComparer.Ordinal)
        || (Split(element, "san:") is { } kind && SanKinds.Contains(kind, StringComparer.Ordinal))
        || (Split(element, "subject:") is { } subjectOid && LooksLikeOid(subjectOid))
        || (Split(element, "issuer:") is { } issuerOid && LooksLikeOid(issuerOid));

    /// <summary>Every address a refusal can suggest, for the message a rejected rule gets.</summary>
    public static string Describe() =>
        $"{string.Join(", ", Whole)}, san:{{{string.Join('|', SanKinds)}}}, subject:{{oid}}, issuer:{{oid}}";

    /// <summary>
    /// The values <paramref name="element"/> resolves to on this certificate — possibly none.
    /// </summary>
    /// <remarks>
    /// A LIST, because a certificate may carry several SANs of one kind and a DN may repeat an RDN. A rule
    /// passes when ANY value matches: an organisation that puts two DNS names in a certificate has not made
    /// it less theirs, and requiring every value to match would refuse certificates the policy was written to
    /// admit.
    /// </remarks>
    public static IReadOnlyList<string> ValuesOf(X509Certificate2 certificate, string element)
    {
        if (element == "serial")
        {
            return [certificate.SerialNumber];
        }

        if (element == "thumbprint")
        {
            return [certificate.Thumbprint];
        }

        if (element == "subject")
        {
            return [certificate.Subject];
        }

        if (element == "issuer")
        {
            return [certificate.Issuer];
        }

        if (Split(element, "san:") is { } kind)
        {
            // The e-mail SAN comes from GetNameInfo rather than the extension, because the framework's SAN
            // parser does not expose it (see SanKinds) — and this is the same call the enveloper uses to
            // decide which address a recipient certificate belongs to, so a rule and an envelope agree about
            // what a certificate's e-mail IS.
            return kind == "email"
                ? certificate.GetNameInfo(X509NameType.EmailName, forIssuer: false) is { Length: > 0 } email
                    ? [email]
                    : []
                : SubjectAlternativeNames(certificate, kind);
        }

        if (Split(element, "subject:") is { } subjectOid)
        {
            return RdnValues(certificate.SubjectName, subjectOid);
        }

        if (Split(element, "issuer:") is { } issuerOid)
        {
            return RdnValues(certificate.IssuerName, issuerOid);
        }

        // Unreachable for a stored policy — the surface refuses an unknown address — but a policy document
        // could be edited by hand, and answering "no values" makes such a rule fail closed rather than throw
        // inside a read.
        return [];
    }

    private static IReadOnlyList<string> RdnValues(X500DistinguishedName name, string oid)
    {
        var values = new List<string>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value == oid && rdn.GetSingleElementValue() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<string> SubjectAlternativeNames(X509Certificate2 certificate, string kind)
    {
        // The SAN extension is parsed by the framework rather than by a regex over its formatted text: the
        // formatted form is culture- and platform-dependent ("DNS Name=" versus "DNS:"), and a rule that
        // matched on one host and not another would be the worst kind of policy bug.
        var extension = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (extension is null)
        {
            return [];
        }

        return kind switch
        {
            "dns" => [.. extension.EnumerateDnsNames()],
            _ => [],
        };
    }

    private static string? Split(string element, string prefix) =>
        element.StartsWith(prefix, StringComparison.Ordinal) && element.Length > prefix.Length
            ? element[prefix.Length..]
            : null;

    private static bool LooksLikeOid(string value) =>
        value.Length > 0 && value.Split('.') is { Length: >= 2 } parts
        && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
}
