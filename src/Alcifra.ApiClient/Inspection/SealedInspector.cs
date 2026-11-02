using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.Xml;
using Alcifra.SealedArchive;
using CmsRecipient = Alcifra.SealedArchive.CmsRecipient;

namespace Alcifra.ApiClient.Inspection;

/// <summary>
/// Reads what a sealed file is (#2007), without a key and without decrypting anything: a bare CMS structure, one inside
/// an S/MIME message, or a sealed archive. One reader for every format, over the parsers the clients already use
/// (<see cref="SmimeEnvelope"/>, the archive's layout, <see cref="CmsRecipients"/>'s recipient rule), never a second one.
/// </summary>
public static class SealedInspector
{
    private const string EnvelopedDataOid = "1.2.840.113549.1.7.3";
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string AuthEnvelopedDataOid = "1.2.840.113549.1.9.16.1.23";

    public static SealedInspection Inspect(byte[] bytes)
    {
        if (bytes is [0x50, 0x4B, 0x03, 0x04, ..])
        {
            return Archive(bytes);
        }

        if (OuterContentType(bytes) is { } outer)
        {
            return Cms(bytes, outer, smimeWrapped: false);
        }

        // An S/MIME message around the CMS, as the server serves an envelope.
        try
        {
            var cms = SmimeEnvelope.Cms(bytes);
            if (cms.Length > 0 && OuterContentType(cms) is { } inner)
            {
                return Cms(cms, inner, smimeWrapped: true) with { Size = bytes.Length };
            }
        }
        catch (Exception e) when (e is EnvelopeNotOpenedException or FormatException or ArgumentException)
        {
        }

        return Empty(SealedKind.NotSealed, bytes.Length);
    }

    private static SealedInspection Cms(byte[] cms, string outer, bool smimeWrapped) => outer switch
    {
        EnvelopedDataOid => Enveloped(cms) with { SmimeWrapped = smimeWrapped },
        SignedDataOid => Signed(cms) with { SmimeWrapped = smimeWrapped },
        AuthEnvelopedDataOid => Empty(SealedKind.AuthEnveloped, cms.Length) with { SmimeWrapped = smimeWrapped, Cms = cms },
        _ => Empty(SealedKind.NotSealed, cms.Length),
    };

    private static SealedInspection Enveloped(byte[] cms)
    {
        var envelope = new EnvelopedCms();
        envelope.Decode(cms);
        var recipients = new List<InspectedRecipient>();
        foreach (var info in envelope.RecipientInfos)
        {
            var reference = info.RecipientIdentifier.Value switch
            {
                X509IssuerSerial id => new CmsRecipient(id.IssuerName, id.SerialNumber, null),
                string ski => new CmsRecipient(null, null, ski),
                _ => new CmsRecipient(null, null, null),
            };
            recipients.Add(info switch
            {
                KeyAgreeRecipientInfo agree => new InspectedRecipient(reference, "key agreement", Name(agree.KeyEncryptionAlgorithm.Oid),
                    agree.OriginatorIdentifierOrKey.Value is PublicKeyInfo originator ? Name(originator.Algorithm.Oid) : null),
                _ => new InspectedRecipient(reference, "key transport", Name(info.KeyEncryptionAlgorithm.Oid), null),
            });
        }

        return new SealedInspection(SealedKind.Enveloped, false, cms.Length, Name(envelope.ContentInfo.ContentType),
            Name(envelope.ContentEncryptionAlgorithm.Oid), recipients, [], [], null, cms);
    }

    private static SealedInspection Signed(byte[] cms)
    {
        var signed = new SignedCms();
        signed.Decode(cms);
        var detached = signed.Detached || signed.ContentInfo.Content.Length == 0;
        var signers = new List<InspectedSigner>();
        foreach (var signer in signed.SignerInfos)
        {
            bool? valid = null;
            if (!detached)
            {
                try
                {
                    signer.CheckSignature(verifySignatureOnly: true);
                    valid = true;
                }
                catch (CryptographicException)
                {
                    valid = false;
                }
            }

            var time = signer.SignedAttributes.OfType<CryptographicAttributeObject>()
                .SelectMany(a => a.Values.OfType<Pkcs9SigningTime>()).Select(t => (DateTimeOffset?)new DateTimeOffset(t.SigningTime)).FirstOrDefault();
            var (issuer, serial) = signer.SignerIdentifier.Value is X509IssuerSerial id ? (id.IssuerName, id.SerialNumber) : (null, null);
            signers.Add(new InspectedSigner(signer.Certificate?.Subject, issuer, serial, Name(signer.DigestAlgorithm), time, valid));
        }

        return new SealedInspection(SealedKind.Signed, false, cms.Length, Name(signed.ContentInfo.ContentType), null, [], signers,
            [.. signed.Certificates.Select(c => c.Subject)], null, cms);
    }

    // The cover is read as it is and its signature checked (not whether its signer is trusted: that is the reader's), and
    // the key envelope gives the recipients.
    private static SealedInspection Archive(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var coverBytes = Read(zip, SealedArchiveLayout.Cover);
        var cover = coverBytes is null ? null : System.Text.Json.JsonSerializer.Deserialize<ArchiveCover>(coverBytes, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var (valid, signer) = (false, (string?)null);
        if (coverBytes is not null && Read(zip, SealedArchiveLayout.CoverSignature) is { } signature)
        {
            var signed = new SignedCms(new ContentInfo(coverBytes), detached: true);
            try
            {
                signed.Decode(signature);
                signer = signed.SignerInfos.Count > 0 ? signed.SignerInfos[0].Certificate?.Subject : null;
                signed.CheckSignature(verifySignatureOnly: true);
                valid = true;
            }
            catch (CryptographicException)
            {
                valid = false;
            }
        }

        var keyEnvelope = Read(zip, SealedArchiveLayout.KeyEnvelope);
        var recipients = keyEnvelope is not null && OuterContentType(keyEnvelope) == EnvelopedDataOid ? Enveloped(keyEnvelope) : null;
        var archive = cover is null ? null : new InspectedArchive(cover.Name, cover.ExportedAt, $"{cover.Source.TenantName} ({cover.Source.ApiRoot})", cover.Recipients, valid, signer);
        return new SealedInspection(SealedKind.Archive, false, bytes.Length, null, recipients?.ContentEncryption,
            recipients?.Recipients ?? [], [], [], archive, keyEnvelope);
    }

    // The ContentInfo's content type: the outer SEQUENCE's first element, read without trusting anything else.
    private static string? OuterContentType(byte[] bytes)
    {
        try
        {
            var reader = new AsnReader(bytes, AsnEncodingRules.BER);
            return reader.ReadSequence().ReadObjectIdentifier();
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? Read(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not { } entry)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static SealedInspection Empty(SealedKind kind, long size) => new(kind, false, size, null, null, [], [], [], null, null);

    private static string Name(Oid? oid) => oid is null ? "?" : string.IsNullOrEmpty(oid.FriendlyName) ? oid.Value ?? "?" : $"{oid.FriendlyName} ({oid.Value})";
}
