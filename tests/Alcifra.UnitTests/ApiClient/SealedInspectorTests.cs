using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Alcifra.ApiClient.Inspection;
using Alcifra.SealedArchive;
using MimeKit;
using ApplicationPkcs7Mime = MimeKit.Cryptography.ApplicationPkcs7Mime;
using CmsSigner = System.Security.Cryptography.Pkcs.CmsSigner;
using SecureMimeType = MimeKit.Cryptography.SecureMimeType;
using SubjectIdentifierType = System.Security.Cryptography.Pkcs.SubjectIdentifierType;
using static Alcifra.UnitTests.SealedArchive.SealedArchiveTestKeys;

namespace Alcifra.UnitTests.ApiClient;

// `alcifra inspect` (#2007, slice 1): what a sealed file is and whom it names, read without any key, for every format —
// an envelope (RSA key transport and ECDH key agreement), one inside an S/MIME message, a signed file, a sealed archive.
public class SealedInspectorTests
{
    private static readonly X509Certificate2 Anna = Rsa("Anna Muster");
    private static readonly X509Certificate2 Tom = Ec("Tom Kern");
    private static readonly X509Certificate2 Stranger = Rsa("Somebody Else");

    [Fact]
    public void An_envelope_names_each_recipient_with_how_the_key_reaches_it_and_matches_the_known_ones()
    {
        var sealedBytes = Alcifra.ApiClient.SealedDocument.Seal("minutes"u8.ToArray(), "text/plain", [PublicOnly(Anna), PublicOnly(Tom), PublicOnly(Stranger)]);

        var inspection = SealedInspector.Inspect(sealedBytes);

        Assert.Equal(SealedKind.Enveloped, inspection.Kind);
        Assert.False(inspection.SmimeWrapped);
        Assert.Equal(3, inspection.Recipients.Count);
        Assert.Contains(inspection.Recipients, r => r.Method == "key transport");
        Assert.Contains(inspection.Recipients, r => r.Method == "key agreement" && r.OriginatorKeyAlgorithm is not null);

        var matched = RecipientMatching.Match(inspection.Recipients, [PublicOnly(Anna), PublicOnly(Tom)]);
        Assert.Equal(2, matched.Count(m => m.Certificate is not null));
        Assert.Null(matched.Single(m => m.Recipient.Reference.Is(Stranger)).Certificate);   // an unknown recipient, by reference only
    }

    [Fact]
    public void A_recipient_named_by_subject_key_identifier_is_matched_by_it()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Ina Bauer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var ina = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var envelope = new EnvelopedCms(new ContentInfo("x"u8.ToArray()));
        envelope.Encrypt(new System.Security.Cryptography.Pkcs.CmsRecipient(SubjectIdentifierType.SubjectKeyIdentifier, PublicOnly(ina)));

        var recipient = Assert.Single(SealedInspector.Inspect(envelope.Encode()).Recipients);

        Assert.NotNull(recipient.Reference.SubjectKeyIdentifier);
        Assert.NotNull(Assert.Single(RecipientMatching.Match([recipient], [PublicOnly(ina)])).Certificate);
    }

    [Fact]
    public void An_envelope_inside_an_S_MIME_message_is_read_as_the_server_serves_it()
    {
        var cms = Alcifra.ApiClient.SealedDocument.Seal("minutes"u8.ToArray(), "text/plain", [PublicOnly(Anna)]);
        using var served = new MemoryStream();
        new MimeMessage { Body = new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(cms)) }.WriteTo(served);

        var inspection = SealedInspector.Inspect(served.ToArray());

        Assert.Equal(SealedKind.Enveloped, inspection.Kind);
        Assert.True(inspection.SmimeWrapped);
        Assert.Single(inspection.Recipients);
    }

    [Fact]
    public void A_signed_file_says_who_signed_and_whether_it_holds_and_a_detached_signature_says_it_needs_the_file()
    {
        var attached = new SignedCms(new ContentInfo("the report"u8.ToArray()));
        attached.ComputeSignature(new CmsSigner(Anna));
        var detached = new SignedCms(new ContentInfo("the report"u8.ToArray()), detached: true);
        detached.ComputeSignature(new CmsSigner(Anna));

        var signer = Assert.Single(SealedInspector.Inspect(attached.Encode()).Signers);
        Assert.Equal(Anna.Subject, signer.Subject);
        Assert.True(signer.SignatureValid);
        Assert.Null(Assert.Single(SealedInspector.Inspect(detached.Encode()).Signers).SignatureValid);
    }

    [Fact]
    public async Task An_archive_shows_its_cover_whether_its_signature_holds_and_whom_its_key_is_sealed_to()
    {
        var installation = Rsa("Alcifra archive signing");
        IReadOnlyList<SealedArchiveRecipient> recipients =
            [new("Tom Kern", ArchiveRecipientKind.Exporter, PublicOnly(Tom)), new("Recovery", ArchiveRecipientKind.Recovery, PublicOnly(Anna))];
        var archive = await WriteAsync(installation, recipients);

        var inspection = SealedInspector.Inspect(archive);
        Assert.Equal(SealedKind.Archive, inspection.Kind);
        Assert.Equal("Road copy", inspection.Archive!.Name);
        Assert.True(inspection.Archive.SignatureValid);
        Assert.Contains("Alcifra archive signing", inspection.Archive.Signer);
        Assert.All(RecipientMatching.Match(inspection.Recipients, [PublicOnly(Tom), PublicOnly(Anna)]), m => Assert.NotNull(m.Certificate));

        var tampered = Rewrite(archive, SealedArchiveLayout.Cover, cover => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(cover).Replace("Road copy", "Road kopy", StringComparison.Ordinal)));
        Assert.False(SealedInspector.Inspect(tampered).Archive!.SignatureValid);
    }

    [Fact]
    public void Anything_else_is_not_a_sealed_file()
    {
        Assert.Equal(SealedKind.NotSealed, SealedInspector.Inspect("just some text"u8.ToArray()).Kind);
    }

    [Fact]
    public void The_ASN_1_tree_walks_every_element_with_its_offset_and_lengths()
    {
        // DER, definite lengths: the outer element covers the file exactly.
        var envelope = new EnvelopedCms(new ContentInfo("x"u8.ToArray()));
        envelope.Encrypt(new System.Security.Cryptography.Pkcs.CmsRecipient(PublicOnly(Anna)));
        var der = envelope.Encode();
        var tree = Asn1Tree.Of(der);
        Assert.Equal((0, 0, "SEQUENCE", true), (tree[0].Offset, tree[0].Depth, tree[0].Tag, tree[0].Constructed));
        Assert.Equal(der.Length, tree[0].HeaderLength + tree[0].Length);
        Assert.Contains("1.2.840.113549.1.7.3", tree[1].Value, StringComparison.Ordinal);

        // BER, as our own sealer streams it: an indefinite outer length (shown "inf"), walked to its end.
        var ber = Asn1Tree.Of(Alcifra.ApiClient.SealedDocument.Seal("minutes"u8.ToArray(), "text/plain", [PublicOnly(Anna)]));
        Assert.Equal(-1, ber[0].Length);
        Assert.Contains("l= inf", Asn1Tree.Lines(ber), StringComparison.Ordinal);
        Assert.Contains(ber, n => n.Tag == "OBJECT" && n.Value!.Contains("1.2.840.113549.1.7.3", StringComparison.Ordinal));
    }
}
