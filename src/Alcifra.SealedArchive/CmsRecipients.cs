using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;

namespace Alcifra.SealedArchive;

/// <summary>One recipient a CMS envelope names, in the clear: by issuer and serial number, or by subject key identifier.</summary>
public sealed record CmsRecipient(string? Issuer, string? Serial, string? SubjectKeyIdentifier)
{
    /// <summary>Whether <paramref name="certificate"/> is this recipient.</summary>
    /// <remarks>The serial compared case-insensitively (a hex string whose casing is the producer's), the issuer as an
    /// ordinal string (both sides render one distinguished name through the same formatter).</remarks>
    public bool Is(X509Certificate2 certificate) =>
        (Serial is not null && Issuer is not null
            && string.Equals(Serial, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Issuer, certificate.Issuer, StringComparison.Ordinal))
        || (SubjectKeyIdentifier is not null
            && certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault() is { SubjectKeyIdentifier: { } ski }
            && string.Equals(SubjectKeyIdentifier, ski, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Who a CMS envelope is addressed to, read without any key (ADR 0964): a password store compares each entry's recipients
/// with its readers' current certificates, on the server, to say which entries need sealing again.
/// </summary>
/// <remarks>
/// Here, in the leaf both the server and the shared client reference, so the side that says "needs re-seal" and the side
/// that re-seals read an envelope's recipients the same way.
/// </remarks>
public static class CmsRecipients
{
    /// <summary>The recipients of a bare CMS <c>EnvelopedData</c>; empty when it cannot be read as one.</summary>
    public static IReadOnlyList<CmsRecipient> Of(byte[] cms)
    {
        try
        {
            var envelope = new EnvelopedCms();
            envelope.Decode(cms);
            var recipients = new List<CmsRecipient>();
            foreach (var info in envelope.RecipientInfos)
            {
                recipients.Add(info.RecipientIdentifier.Value switch
                {
                    X509IssuerSerial id => new CmsRecipient(id.IssuerName, id.SerialNumber, null),
                    string ski => new CmsRecipient(null, null, ski),
                    _ => new CmsRecipient(null, null, null),
                });
            }

            return recipients;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether an envelope addressed to <paramref name="recipients"/> is addressed to exactly <paramref name="certificates"/>:
    /// every certificate is a recipient, and every recipient is one of the certificates.
    /// </summary>
    public static bool AddressedToExactly(IReadOnlyList<CmsRecipient> recipients, IReadOnlyList<X509Certificate2> certificates) =>
        certificates.All(c => recipients.Any(r => r.Is(c)))
        && recipients.All(r => certificates.Any(r.Is));
}
