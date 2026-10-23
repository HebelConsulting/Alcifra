using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimplArchive.Infrastructure.Certificates.Attestation;

/// <summary>
/// Verifies PIV-form attestation (#1790, ADR 0963): the token issues a certificate for one key slot, signed by its own
/// attestation key (the PIV "F9" slot), whose certificate chains to the manufacturer's attestation root. Verified means
/// three things, each refused by name when it fails: every signature in the chain holds, the chain ends at a root the
/// caller supplied, and the key it attests IS the key of the certificate being enrolled.
/// </summary>
/// <remarks>
/// <para>
/// <b>An imported key cannot be attested</b>: the token attests only what it generated, so a refusal here is also how a
/// key that was ever outside the token shows up.
/// </para>
/// <para>
/// <b>The roots are passed in, all of them, switched on or not.</b> The verifier answers which root the evidence proves;
/// whether that root is trusted today is the reader answer's question, so switching a root never has to touch a
/// certificate. Intermediates come from the evidence and from the catalogue's published intermediates, which are
/// chain-building help and never trusted on their own.
/// </para>
/// <para>
/// Validity periods are not checked: an attestation is a statement made when the token was manufactured and the key
/// generated, and the manufacturer's attestation certificates are issued for the token's lifetime.
/// </para>
/// </remarks>
public static class PivAttestationVerifier
{
    /// <summary>The format name a client sends for this form of evidence.</summary>
    public const string Format = "piv";

    // The PIV attestation extensions in the manufacturer's private arc: firmware as three raw bytes, serial as an INTEGER.
    private const string FirmwareOid = "1.3.6.1.4.1.41482.3.3";
    private const string SerialOid = "1.3.6.1.4.1.41482.3.7";

    // A token's chain is three or four certificates; anything longer is not one, and the walk must end.
    private const int MaximumDepth = 6;

    public static AttestationVerdict Verify(
        AttestationEvidence evidence, X509Certificate2 enrolled, IReadOnlyList<AttestationAnchor> roots, IReadOnlyList<X509Certificate2> helpers)
    {
        if (!string.Equals(evidence.Format, Format, StringComparison.OrdinalIgnoreCase))
        {
            return new(AttestationOutcome.Malformed, $"The attestation format '{evidence.Format}' is not one this installation verifies; it verifies '{Format}'.");
        }

        X509Certificate2 slot;
        X509Certificate2Collection supplied;
        try
        {
            slot = X509Certificate2.CreateFromPem(evidence.AttestationCertificatePem);
            supplied = [];
            supplied.ImportFromPem(evidence.IntermediateCertificatePem);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return new(AttestationOutcome.Malformed, "The attestation certificates cannot be read as PEM certificates.");
        }

        using (slot)
        {
            if (supplied.Count == 0)
            {
                return new(AttestationOutcome.Malformed, "The attestation carries no token attestation certificate (the one that signed the slot's).");
            }

            var device = supplied[0];
            if (!CertificateSignature.IsSignedBy(slot, device))
            {
                return new(AttestationOutcome.SignatureInvalid, "The slot's attestation certificate was not signed by the token's attestation certificate sent with it.");
            }

            var walk = Walk(device, [.. supplied.Skip(1), .. helpers], roots);
            if (walk.Outcome != AttestationOutcome.Verified)
            {
                return walk;
            }

            if (!slot.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(enrolled.PublicKey.ExportSubjectPublicKeyInfo()))
            {
                return new(AttestationOutcome.KeyMismatch, "The attestation is genuine but attests a different key than this certificate's.");
            }

            return walk with
            {
                DeviceSerial = Serial(slot) ?? Serial(device) ?? string.Empty,
                FirmwareVersion = Firmware(slot) ?? Firmware(device) ?? string.Empty,
            };
        }
    }

    // From the token's certificate up: a root that signed the current certificate ends it; otherwise an intermediate that
    // did continues it. A certificate NAMING a known issuer whose signature does not verify is tampering, not an unknown root.
    private static AttestationVerdict Walk(X509Certificate2 current, IReadOnlyList<X509Certificate2> pool, IReadOnlyList<AttestationAnchor> roots)
    {
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            var named = false;
            foreach (var root in roots)
            {
                if (current.IssuerName.RawData.AsSpan().SequenceEqual(root.Certificate.SubjectName.RawData))
                {
                    named = true;
                    if (CertificateSignature.IsSignedBy(current, root.Certificate))
                    {
                        return new(AttestationOutcome.Verified, $"Attested to {root.Certificate.Subject}.", root.Thumbprint);
                    }
                }
            }

            var next = pool.FirstOrDefault(c => !ReferenceEquals(c, current) && CertificateSignature.IsSignedBy(current, c));
            if (next is null)
            {
                named |= pool.Any(c => current.IssuerName.RawData.AsSpan().SequenceEqual(c.SubjectName.RawData));
                return named
                    ? new(AttestationOutcome.SignatureInvalid, $"The certificate '{current.Subject}' names its issuer, but the issuer's key did not sign it.")
                    : new(AttestationOutcome.RootUnknown, $"The attestation chain ends at '{current.Issuer}', which is not an attestation root this installation knows.");
            }

            current = next;
        }

        return new(AttestationOutcome.Malformed, "The attestation chain is longer than any token's.");
    }

    private static string? Firmware(X509Certificate2 certificate) =>
        certificate.Extensions[FirmwareOid]?.RawData is { Length: 3 } raw
            ? string.Create(CultureInfo.InvariantCulture, $"{raw[0]}.{raw[1]}.{raw[2]}")
            : null;

    private static string? Serial(X509Certificate2 certificate)
    {
        if (certificate.Extensions[SerialOid]?.RawData is not { Length: > 0 } raw)
        {
            return null;
        }

        try
        {
            return new AsnReader(raw, AsnEncodingRules.DER).ReadInteger().ToString(CultureInfo.InvariantCulture);
        }
        catch (AsnContentException)
        {
            // Not wrapped as an INTEGER: read the bytes as the unsigned big-endian number they are.
            return new BigInteger(raw, isUnsigned: true, isBigEndian: true).ToString(CultureInfo.InvariantCulture);
        }
    }
}
