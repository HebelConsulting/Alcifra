using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;

namespace Alcifra.ApiClient;

/// <summary>
/// Opens an envelope with a key held in software (a certificate with its private key on this computer), RSA key transport
/// and ECDH key agreement alike, through the mail library's S/MIME context. Moved here from the desktop so the desktop's
/// funnel and `alcifra inspect --try-open` (#2007) open with one implementation.
/// </summary>
public static class SoftwareKeyEnvelope
{
    /// <summary>
    /// The content, or null when none of <paramref name="candidates"/> opens it (declining, so a card can be tried next).
    /// The caller passes only certificates the envelope is addressed to: importing one may ask the platform for its key.
    /// </summary>
    /// <param name="skipped">Told of a certificate the platform would not hand over (macOS keychain authorization).</param>
    public static (byte[] Bytes, string ContentType)? TryOpen(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<X509Certificate2> candidates, Action<X509Certificate2, Exception>? skipped = null) =>
        Opened<(byte[] Bytes, string ContentType)?>(candidates, skipped, context => SmimeEnvelope.Payload(enveloped.Decrypt(context)));

    /// <summary>The same opening, also handing on the file name sealed with the document (#2312).</summary>
    public static (byte[] Bytes, string ContentType, string? FileName)? TryOpenNamed(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<X509Certificate2> candidates, Action<X509Certificate2, Exception>? skipped = null) =>
        Opened<(byte[] Bytes, string ContentType, string? FileName)?>(candidates, skipped, context => SmimeEnvelope.Opened(enveloped.Decrypt(context)));

    /// <summary>
    /// The decrypted content AS IT IS, not read as a MIME part: an envelope from another producer need not carry one, and
    /// "does this key open it?" (`inspect --try-open`) is a question about the key, not the framing. Null declines.
    /// </summary>
    public static byte[]? TryOpenRaw(byte[] cms, IReadOnlyList<X509Certificate2> candidates, Action<X509Certificate2, Exception>? skipped = null) =>
        Opened(candidates, skipped, context =>
        {
            using var output = new MemoryStream();
            context.DecryptTo(new MemoryStream(cms), output);
            return output.ToArray();
        });

    private static T? Opened<T>(IReadOnlyList<X509Certificate2> candidates, Action<X509Certificate2, Exception>? skipped, Func<SecureMimeContext, T> open)
    {
        if (candidates.Count == 0)
        {
            return default;
        }

        using var context = new TemporarySecureMimeContext();
        var imported = 0;
        foreach (var certificate in candidates)
        {
            try
            {
                context.Import(certificate);
                imported++;
            }
            catch (Exception e)
            {
                // A certificate the platform will not hand over (OBSERVED on macOS: "Unable to obtain authorization for
                // this operation"): skipped, so a card can still open what this key could not.
                skipped?.Invoke(certificate, e);
            }
        }

        if (imported == 0)
        {
            return default;
        }

        try
        {
            return open(context);
        }
        // CmsException is matched BY NAME: MimeKit decrypts through BouncyCastle, which reports "A suitable private key
        // could not be found for decrypting" as Org.BouncyCastle.Cms.CmsException. Naming the type would bind this
        // library to a package it only has transitively; catching everything would report genuine defects as a
        // missing key. Declining, not throwing: "no software key opens this" is the case the card exists for.
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException
            || e.GetType().Name == "CmsException")
        {
            return default;
        }
    }
}
