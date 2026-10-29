using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.ExternalLinks;
using Alcifra.Api.Errors.Exceptions.OfflineArchives;

namespace Alcifra.Api.OfflineArchives;

/// <summary>
/// Whether a PEM may be the tenant's recovery certificate (ADR 0960), checked when an administrator sets it, while they
/// are there to fix it: a recovery path found broken years later is no recovery path.
/// </summary>
public static class RecoveryCertificate
{
    /// <summary>What the setting stores and shows: the certificate alone, its thumbprint, its holder and its expiry.</summary>
    public readonly record struct Accepted(string CertificatePem, string Thumbprint, string Subject, DateTimeOffset NotAfter);

    /// <summary>The accepted certificate, normalised to its one PEM block, or <see cref="InvalidRecoveryCertificateException"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>A private key in the upload is refused outright</b>, not stripped: the owner's rule is that the key never touches
    /// Alcifra, and silently discarding it would hide that it just did. The administrator learns the file was wrong.
    /// </para>
    /// <para>
    /// <b>Exactly one certificate</b>, so it is clear which key is the escrow; <b>RSA or elliptic-curve</b>, asked the way
    /// every recipient is (<see cref="RecipientCertificate.Validate"/>); and <b>not already expired</b>, unlike a reader's
    /// certificate: an escrow that is expired on the day it is set is almost certainly the wrong file. A certificate that
    /// expires LATER keeps opening the archives sealed to it, because the private key outlives its certificate.
    /// </para>
    /// </remarks>
    public static Accepted Validate(string pem, DateTimeOffset now)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidRecoveryCertificateException("the upload carries a private key, which never belongs here");
        }

        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem.Replace("-----END CERTIFICATE-----", "-----END CERTIFICATE-----\n", StringComparison.Ordinal));
        }
        catch (CryptographicException)
        {
            throw new InvalidRecoveryCertificateException("it is not a readable PEM-encoded X.509 certificate");
        }

        if (collection.Count != 1)
        {
            throw new InvalidRecoveryCertificateException(collection.Count == 0
                ? "it holds no certificate"
                : $"it holds {collection.Count} certificates, and the recovery certificate is one");
        }

        using var certificate = collection[0];
        var normalised = certificate.ExportCertificatePem();
        try
        {
            RecipientCertificate.Validate(normalised);
        }
        catch (InvalidRecipientCertificateException)
        {
            throw new InvalidRecoveryCertificateException("its key can receive no envelope (neither RSA nor elliptic-curve)");
        }

        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (notAfter <= now)
        {
            throw new InvalidRecoveryCertificateException($"it expired on {notAfter:yyyy-MM-dd}");
        }

        return new Accepted(normalised, certificate.GetCertHashString(HashAlgorithmName.SHA256), certificate.Subject, notAfter);
    }
}
