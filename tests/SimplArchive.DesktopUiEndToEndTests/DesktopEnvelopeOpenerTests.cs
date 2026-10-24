using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.DesktopClient.Services;
using SimplArchive.ApiClient;

namespace SimplArchive.UiEndToEndTests;

// The desktop opens the envelope a strict-tier read arrives in (#1353, ADR 0828).
//
// This is the issue's acceptance step ONE, and its order is the point: prove decryption against a SOFTWARE
// certificate before involving a card. Doing the card first means a failure has two candidate causes — "can
// .NET decrypt at all" and "can .NET drive a token-backed key" — with no way to separate them.
//
// Built against a real envelope produced the way the server produces one (MimeKit, CMS EnvelopedData, RSA key
// transport), rather than a fixture blob: a test that constructs its own idea of the format proves the client
// agrees with the TEST, which is the failure mode of every replay built by analogy.
[Collection(UiCollection.Name)]
public class DesktopEnvelopeOpenerTests : IDisposable
{
    private const string Marker = "DESKTOP-OPENED-THE-ENVELOPE";

    private readonly Func<X509Certificate2Collection> _originalKeys = EnvelopeOpener.Keys;
    private readonly IReadOnlyList<EnvelopeOpener.Opener> _originalOpeners = EnvelopeOpener.Openers;
    private readonly Func<IReadOnlyList<X509Certificate2>> _originalCards = EnvelopeOpener.CardCertificatesSeen;
    private readonly Func<CancellationToken, Task<EnrolledKeysAnswer>> _originalSignedIn = EnvelopeOpener.SignedIn;

    public void Dispose()
    {
        EnvelopeOpener.Keys = _originalKeys;
        EnvelopeOpener.Openers = _originalOpeners;
        EnvelopeOpener.CardCertificatesSeen = _originalCards;
        EnvelopeOpener.SignedIn = _originalSignedIn;
    }

    [Fact]
    public async Task An_envelope_addressed_to_a_key_this_machine_holds_opens_into_the_document()
    {
        var recipient = NewCertificate();
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [recipient];
        Enrolled(recipient);

        var (bytes, contentType) = await EnvelopeOpener.OpenAsync(
            Envelope(recipient, Encoding.ASCII.GetBytes(Marker), "application/pdf", "invoice.pdf"),
            "application/pkcs7-mime");

        Assert.Equal(Marker, Encoding.ASCII.GetString(bytes));

        // The INNER type, not the wrapper's: a PDF announced as pkcs7-mime would be sniffed, mis-rendered or
        // refused by every consumer downstream of this funnel.
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public async Task An_ordinary_response_is_returned_exactly_as_it_arrived()
    {
        // The anti-vacuous half, and the one that matters for every tenant that is NOT strict: this hook sits
        // in the funnel every read passes through, so "does nothing unless it is an envelope" is a property of
        // the whole client rather than of this method.
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [];
        var plain = Encoding.ASCII.GetBytes("%PDF-1.7 an ordinary document");

        var (bytes, contentType) = await EnvelopeOpener.OpenAsync(plain, "application/pdf");

        Assert.Same(plain, bytes);
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public async Task An_envelope_addressed_to_somebody_else_refuses_rather_than_returning_the_bytes()
    {
        // Never a fall back to what arrived. Handing the caller undecrypted CMS would render as a corrupt
        // document and send them looking for a damaged file instead of a missing key.
        var addressee = NewCertificate();
        var mine = NewCertificate();
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [mine];
        Enrolled(mine);

        // The user's own key is here and enrolled, and the envelope names none of the user's keys (ADR 0969).
        var thrown = await Assert.ThrowsAsync<EnvelopeStaysSealedException>(() => EnvelopeOpener.OpenAsync(
            Envelope(addressee, Encoding.ASCII.GetBytes(Marker), "application/pdf", "invoice.pdf"),
            "application/pkcs7-mime"));
        Assert.Equal(SealedContentState.SealedForOtherKeys, thrown.Outcome.State);

        // The message now names which of the three things is missing (#1353 acceptance 3), and this host has
        // no card in a reader — so it must say so rather than blaming the document.
        Assert.Contains("addressed to", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_key_at_all_it_says_that_rather_than_that_the_document_is_wrong()
    {
        // The commonest real case — a reader who has not enrolled — and the message decides whether they go
        // looking for their certificate or for a broken file.
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [];
        var absent = NewCertificate();
        Enrolled(absent);

        // No key at hand, sealed to a key enrolled for the user: "sealed", naming the key to insert (ADR 0969).
        var thrown = await Assert.ThrowsAsync<EnvelopeStaysSealedException>(() => EnvelopeOpener.OpenAsync(
            Envelope(absent, [1, 2, 3], "application/pdf", "x.pdf"), "application/pkcs7-mime"));
        Assert.Equal(new SealedOutcome(SealedContentState.Sealed, "Office key"), thrown.Outcome);

        Assert.Contains("addressed to", thrown.Message, StringComparison.Ordinal);
    }

    // ---- Only the signed-in user's keys (ADR 0969) --------------------------------------------------------

    [Fact]
    public async Task A_key_of_somebody_else_in_this_store_is_not_used_even_though_it_could_open_the_envelope()
    {
        // The administrator's case: florian's key is on this machine, crypt is signed in, and the envelope is
        // florian's. The key would open it; it must not be touched.
        var florians = NewCertificate();
        var crypts = NewCertificate();
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [florians];
        Enrolled(crypts);

        var thrown = await Assert.ThrowsAsync<EnvelopeStaysSealedException>(() => EnvelopeOpener.OpenAsync(
            Envelope(florians, Encoding.ASCII.GetBytes(Marker), "application/pdf", "invoice.pdf"), "application/pkcs7-mime"));

        Assert.Equal(SealedOutcome.NotEnrolled, thrown.Outcome);
    }

    [Fact]
    public async Task Without_a_session_no_key_is_used_and_it_says_the_enrolled_keys_could_not_be_checked()
    {
        var recipient = NewCertificate();
        SoftwareOnly();
        EnvelopeOpener.Keys = () => [recipient];
        EnvelopeOpener.SignedIn = EnvelopeOpener.NoSession;

        var thrown = await Assert.ThrowsAsync<EnvelopeStaysSealedException>(() => EnvelopeOpener.OpenAsync(
            Envelope(recipient, Encoding.ASCII.GetBytes(Marker), "application/pdf", "invoice.pdf"), "application/pkcs7-mime"));

        Assert.Equal(SealedOutcome.Unknown, thrown.Outcome);
    }

    [Fact]
    public void A_card_certificate_that_is_an_addressee_but_not_enrolled_for_the_user_is_not_selected_so_no_PIN_is_asked()
    {
        // The card opener opens a logged-in session — the PIN prompt — only for the certificates it selects here.
        var florians = NewCertificate();
        var recipients = EnvelopeRecipients.Of(SmimeEnvelope.Unwrap(Envelope(florians, [1], "text/plain", "x.txt")));

        Assert.False(CardEnvelopeOpener.Selects(florians, recipients, _ => false));
        Assert.True(CardEnvelopeOpener.Selects(florians, recipients, _ => true));
        Assert.False(CardEnvelopeOpener.Selects(NewCertificate(), recipients, _ => true));   // not an addressee
    }

    private static void Enrolled(params X509Certificate2[] keys) =>
        EnvelopeOpener.SignedIn = _ => Task.FromResult(new EnrolledKeysAnswer([.. keys.Select(k => EnrolledKey.Of(k, "Office key"))]));

    /// <summary>
    /// Only the software opener, so these tests are about decryption rather than about this host's hardware.
    /// </summary>
    /// <remarks>
    /// Without it, the card opener would run on a developer's machine with a reader and not on CI, which makes
    /// the same assertion mean two different things — the shape that produces a test nobody trusts.
    /// </remarks>
    private static void SoftwareOnly()
    {
        EnvelopeOpener.Openers = [EnvelopeOpener.Openers[0]];
        EnvelopeOpener.CardCertificatesSeen = () => [];
    }

    /// <summary>An envelope built the way the server builds one — same library, same shape.</summary>
    private static byte[] Envelope(X509Certificate2 recipient, byte[] content, string contentType, string fileName)
    {
        var message = new MimeMessage
        {
            Subject = Path.GetFileNameWithoutExtension(fileName),
            Body = new MimePart(ContentType.Parse(contentType))
            {
                Content = new MimeContent(new MemoryStream(content)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        // AES-256 STATED EXPLICITLY, because `new CmsRecipient(certificate)` alone does not get it: a bare
        // certificate advertises no S/MIME capabilities and MimeKit then falls back to 3DES. A fixture built
        // the easy way would have proven this client opens a cipher the server never sends.
        //
        // The server's own SmimeRecipient.For states the same preference, and SmimeContentCipherTests pins it
        // by decoding a real envelope's OID rather than by reading the list — so if production's cipher ever
        // moves, that test fails and names the change, and this fixture follows it. Duplicated rather than
        // referenced because linking Infrastructure here drags EF and Npgsql into a desktop test project and
        // collides with its own pins: a one-line duplicate with an anchor beats that trade.
        using var context = new TemporarySecureMimeContext();
        message.Body = ApplicationPkcs7Mime.Encrypt(
            context,
            new CmsRecipientCollection
            {
                new CmsRecipient(recipient) { EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] },
            },
            message.Body);

        using var output = new MemoryStream();
        message.WriteTo(output);
        return output.ToArray();
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=reader-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Round-tripped through PKCS#12 so the instance carries a usable private key on every platform —
        // CreateSelfSigned's handle is not always one MimeKit can import.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "t"), "t",
            X509KeyStorageFlags.Exportable);
    }
}
