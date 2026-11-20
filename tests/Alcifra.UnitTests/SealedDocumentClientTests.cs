using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Domain.Masks;

namespace Alcifra.UnitTests;

/// <summary>
/// A sealed document's content, sealed on the device and opened through the same funnel every read uses (ADR 0975).
/// </summary>
/// <remarks>
/// The wire shape is the thing worth pinning, because it has two ends that live in different layers: the core's shape
/// check decides at the door from the first bytes, and the clients' one opener must then find an envelope in what was
/// served. A unit test is the only place both halves can be asked of the same bytes without a host.
/// </remarks>
public class SealedDocumentClientTests
{
    [Fact]
    public void The_gaps_name_the_caller_and_every_member_the_seal_will_leave_out()
    {
        // #2321's warning reads the server's SAVED audience: who holds no certificate (the server says so per recipient),
        // and whether the caller is one of them. Sealing still works — it needs only the others' public certificates —
        // which is exactly why the person has to be told before the seal.
        var anna = Guid.NewGuid();
        var tom = Guid.NewGuid();
        var eve = Guid.NewGuid();
        var audience = AudienceWith(("Anna", anna, HasKey: true), ("Tom", tom, HasKey: false), ("Eve", eve, HasKey: true));

        // The caller (Tom) holds no key: their own gap, and Tom is not repeated among the members.
        var tomGaps = audience.GapsFor(tom);
        Assert.True(tomGaps.CallerWithoutKey);
        Assert.Empty(tomGaps.MembersWithoutKey);
        Assert.True(tomGaps.Any);

        // A caller with a key sees the member without one, by name.
        var annaGaps = audience.GapsFor(anna);
        Assert.False(annaGaps.CallerWithoutKey);
        Assert.Equal(["Tom"], annaGaps.MembersWithoutKey);
        Assert.True(annaGaps.Any);

        // Every member holds a key — and an unknown caller (a script without whoami) — warn about nothing.
        var complete = AudienceWith(("Anna", anna, HasKey: true), ("Eve", eve, HasKey: true));
        Assert.False(complete.GapsFor(Guid.NewGuid()).Any);
        Assert.False(complete.GapsFor(null).Any);
    }

    private static Alcifra.ApiClient.ContentAudienceInfo AudienceWith(params (string Name, Guid UserId, bool HasKey)[] recipients) =>
        new(
            DocumentId: Guid.NewGuid(),
            IsStore: false,
            IsPersonal: false,
            ETag: "\"t\"",
            CanManage: true,
            CanAppointManager: false,
            IsMember: true,
            Members: [.. recipients.Select(r => new Alcifra.ApiClient.AudienceMemberInfo(r.UserId, null, r.Name, false, true))],
            Recipients: [.. recipients.Select(r => new Alcifra.ApiClient.AudienceRecipientInfo(
                r.UserId, r.Name, false, r.HasKey ? ["-----BEGIN CERTIFICATE-----"] : []))],
            Candidates: [],
            Links: Alcifra.ApiClient.LinkMap.Empty);

    [Fact]
    public void The_clients_mask_id_is_the_cores_well_known_one()
    {
        // The client cannot reference the server's Domain, so the constant is mirrored. This is the mirror's lock:
        // an id drifting would create documents wearing a mask the sealed-content rules never heard of — a document
        // that LOOKS sealed and is not.
        Assert.Equal(WellKnownMaskIds.SealedDocument, Alcifra.ApiClient.SealedDocument.MaskId);
    }

    [Fact]
    public void A_sealed_document_is_bare_CMS_its_own_funnel_opens_and_the_audience_key_reads()
    {
        using var certificate = NewCertificate("board@example.test");
        var content = "the board pack"u8.ToArray();

        var sealedBytes = Alcifra.ApiClient.SealedDocument.Seal(content, "application/pdf", [certificate]);

        // BARE CMS, not an S/MIME message: the core's door reads the DER header (CmsEnvelopeShape), so an S/MIME
        // wrapper is refused there. Asserted on the bytes rather than by inspection, because this is the contract
        // between the client and `PasswordEntryVersions.GuardAsync` — and a wrapper here fails ONLY in production.
        Assert.Equal(0x30, sealedBytes[0]);

        // ...and the SAME funnel the desktop and phone read every served envelope through still opens it.
        var enveloped = Alcifra.ApiClient.SmimeEnvelope.Unwrap(sealedBytes);
        var cms = Alcifra.ApiClient.SmimeEnvelope.Cms(enveloped);
        Assert.Equal(sealedBytes, cms);

        var decoder = new EnvelopedCms();
        decoder.Decode(cms);
        decoder.Decrypt(new X509Certificate2Collection(certificate));
        var (bytes, contentType) = Alcifra.ApiClient.SmimeEnvelope.Payload(decoder.ContentInfo.Content);

        // The payload is a MIME PART, so the content type survives the envelope — a bare byte[] would come back as
        // whatever the MIME reader guessed.
        Assert.Equal(content, bytes);
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public void A_document_without_an_audience_is_refused_before_anything_is_sealed()
    {
        var refused = Assert.Throws<Alcifra.ApiClient.SealedDocumentAudienceRequiredException>(() =>
            Alcifra.ApiClient.SealedDocument.Seal("content"u8.ToArray(), "text/plain", []));

        Assert.Contains("at least one", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static X509Certificate2 NewCertificate(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // Re-loaded so the private key is attached in the way the CMS decryptor expects on every platform.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
