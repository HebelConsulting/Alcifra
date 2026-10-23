using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimplArchive.Infrastructure.Certificates.Attestation;

/// <summary>
/// Whether one certificate's signature was made by another's key (#1790, ADR 0963), checked by hand from the
/// certificate's own bytes rather than by a platform chain builder.
/// </summary>
/// <remarks>
/// <b>Why not <see cref="X509Chain"/>.</b> An attestation chain is not a TLS chain: a token's attestation certificate need
/// not carry the CA flags a chain builder insists on, and the platform's builder differs between the server (Linux) and a
/// developer's machine, so the same evidence could verify on one and not the other. What attestation needs is narrower
/// and exact: the issuer's name matches and the issuer's key made the signature over the to-be-signed bytes.
/// </remarks>
public static class CertificateSignature
{
    /// <summary>True when <paramref name="issuer"/> names and signed <paramref name="subject"/>.</summary>
    public static bool IsSignedBy(X509Certificate2 subject, X509Certificate2 issuer)
    {
        if (!subject.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData))
        {
            return false;
        }

        try
        {
            var certificate = new AsnReader(subject.RawData, AsnEncodingRules.DER).ReadSequence();
            var toBeSigned = certificate.ReadEncodedValue();
            var algorithm = certificate.ReadSequence().ReadObjectIdentifier();
            var signature = certificate.ReadBitString(out _);
            return Verify(algorithm, toBeSigned.Span, signature, issuer);
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException)
        {
            return false;
        }
    }

    private static bool Verify(string algorithm, ReadOnlySpan<byte> data, byte[] signature, X509Certificate2 issuer) => algorithm switch
    {
        "1.2.840.113549.1.1.11" => Rsa(issuer, data, signature, HashAlgorithmName.SHA256),
        "1.2.840.113549.1.1.12" => Rsa(issuer, data, signature, HashAlgorithmName.SHA384),
        "1.2.840.113549.1.1.13" => Rsa(issuer, data, signature, HashAlgorithmName.SHA512),
        "1.2.840.10045.4.3.2" => EcDsa(issuer, data, signature, HashAlgorithmName.SHA256),
        "1.2.840.10045.4.3.3" => EcDsa(issuer, data, signature, HashAlgorithmName.SHA384),
        "1.2.840.10045.4.3.4" => EcDsa(issuer, data, signature, HashAlgorithmName.SHA512),
        // SHA-1, PSS and anything newer: not accepted. No attestation format we verify uses them, and an algorithm we
        // do not check is an algorithm we would be trusting blindly.
        _ => false,
    };

    private static bool Rsa(X509Certificate2 issuer, ReadOnlySpan<byte> data, byte[] signature, HashAlgorithmName hash)
    {
        using var key = issuer.GetRSAPublicKey();
        return key is not null && key.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1);
    }

    private static bool EcDsa(X509Certificate2 issuer, ReadOnlySpan<byte> data, byte[] signature, HashAlgorithmName hash)
    {
        using var key = issuer.GetECDsaPublicKey();
        return key is not null && key.VerifyData(data, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }
}
