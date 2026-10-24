using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.UiEndToEndTests;

// A sealed document this machine cannot open is a STATE of the preview pane, never a spinner (owner, 2026-10-24).
//
// The defect: the pane said "Loading…" for ever and the reason went to the status line as an error, because the
// envelope's refusal escaped the preview and was caught by the selection load, which reported it and left the pane
// as it was. The states it now shows (ADR 0969) — sealed (insert your key), sealed for other keys, this key is not
// enrolled for you, your enrolled keys could not be checked — are decided by the shared rule (SealedContent), which the
// phone uses; and only a key enrolled for the signed-in user is ever used.
//
// Driven through the REAL funnel: a preview URL on "our installation" served by a handler that answers a real CMS
// envelope as application/pkcs7-mime, so the content funnel, the desktop opener and the pane all run as shipped.
[Collection(UiCollection.Name)]
public class DesktopSealedPreviewTests : IDisposable
{
    private const string Installation = "http://sealed-preview.test";

    private readonly Func<X509Certificate2Collection> _keys = EnvelopeOpener.Keys;
    private readonly IReadOnlyList<EnvelopeOpener.Opener> _openers = EnvelopeOpener.Openers;
    private readonly Func<IReadOnlyList<X509Certificate2>> _cards = EnvelopeOpener.CardCertificatesSeen;
    private readonly Func<bool> _declined = EnvelopeOpener.PromptDeclined;
    private readonly string _root = DesktopClientOptions.ApiBaseUrl;   // also runs the options' static setup: the desktop opener
    private readonly HttpClient? _authenticated = ApiCore.Authenticated;
    private readonly Func<CancellationToken, Task<EnrolledKeysAnswer>> _signedIn = EnvelopeOpener.SignedIn;

    public void Dispose()
    {
        EnvelopeOpener.Keys = _keys;
        EnvelopeOpener.Openers = _openers;
        EnvelopeOpener.CardCertificatesSeen = _cards;
        EnvelopeOpener.PromptDeclined = _declined;
        EnvelopeOpener.SignedIn = _signedIn;
        DesktopClientOptions.ApiBaseUrl = _root;
        ApiCore.Authenticated = _authenticated;
    }

    [Fact]
    public async Task Sealed_to_an_enrolled_key_that_is_not_here_the_pane_names_the_key_to_insert()
    {
        var office = NewCertificate();
        var (preview, status) = Serving(Envelope(office));
        NoKeys();
        Enrolled(("Office key", office));

        await preview.RenderAsync(APreview());

        Assert.Equal("This document is sealed. Insert your key “Office key”.", Strings.Get("PvSealedInsertKey", English).Replace("{0}", "Office key"));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Get("PvSealedInsertKey"), "Office key"), preview.PreviewPlaceholder);
        Assert.Equal(SealedContentState.Sealed, preview.SealedState);
        Assert.Null(preview.SealedSubline);
        Assert.True(preview.CanOpenSealed);
        Assert.Empty(status.Reports);   // the status line stays quiet: nothing failed
    }

    [Fact]
    public async Task Sealed_to_none_of_the_users_enrolled_keys_the_pane_says_sealed_for_other_keys()
    {
        var (preview, status) = Serving(Envelope(NewCertificate()));
        NoKeys();
        var mine = NewCertificate();
        EnvelopeOpener.Keys = () => [mine];   // the user's own key IS here
        Enrolled(("Office key", mine));

        await preview.RenderAsync(APreview());

        Assert.Equal(Strings.Get("PvSealedForOtherKeys"), preview.PreviewPlaceholder);
        Assert.Equal(Strings.Get("PvNoneEnrolledAmongThem"), preview.SealedSubline);
        Assert.Equal(SealedContentState.SealedForOtherKeys, preview.SealedState);
        Assert.True(preview.CanOpenSealed);   // as on the phone (owner, 2026-10-24)
        Assert.Empty(status.Reports);
    }

    [Fact]
    public async Task A_key_of_somebody_else_that_could_open_it_is_not_used_and_the_pane_says_so()
    {
        // florian's key on crypt's machine: the envelope is florian's and the key would open it.
        var florians = NewCertificate();
        var (preview, status) = Serving(Envelope(florians));
        NoKeys();
        EnvelopeOpener.Keys = () => [florians];
        Enrolled(("Crypt's key", NewCertificate()));

        await preview.RenderAsync(APreview());

        Assert.Null(preview.PreviewText);   // not opened
        Assert.Equal(Strings.Get("PvKeyNotEnrolledForYou"), preview.PreviewPlaceholder);
        Assert.Equal(Strings.Get("PvKeyNotEnrolledForYouHint"), preview.SealedSubline);
        Assert.True(preview.CanOpenSealed);
        Assert.Empty(status.Reports);
    }

    [Fact]
    public async Task Without_the_users_enrolled_keys_no_key_is_used_and_the_pane_says_they_could_not_be_checked()
    {
        var recipient = NewCertificate();
        var (preview, _) = Serving(Envelope(recipient));
        NoKeys();
        EnvelopeOpener.Keys = () => [recipient];
        EnvelopeOpener.SignedIn = EnvelopeOpener.NoSession;

        await preview.RenderAsync(APreview());

        Assert.Null(preview.PreviewText);
        Assert.Equal(Strings.Get("PvEnrolledKeysUnknown"), preview.PreviewPlaceholder);
        Assert.Equal(Strings.Get("PvEnrolledKeysUnknownHint"), preview.SealedSubline);
    }

    [Fact]
    public async Task A_cancelled_PIN_prompt_shows_the_sealed_state_with_its_action_not_an_error()
    {
        var addressee = NewCertificate();
        var (preview, status) = Serving(Envelope(addressee));
        NoKeys();
        EnvelopeOpener.CardCertificatesSeen = () => [addressee];   // the right card, whose PIN prompt was cancelled
        EnvelopeOpener.PromptDeclined = () => true;
        Enrolled(("Office key", addressee));

        await preview.RenderAsync(APreview());

        Assert.Equal(SealedContentState.Sealed, preview.SealedState);
        Assert.True(preview.CanOpenSealed);
        Assert.Empty(status.Reports);
    }

    [Fact]
    public async Task Open_with_my_key_reads_the_document_again_and_shows_it_once_the_key_is_there()
    {
        var addressee = NewCertificate();
        var (preview, _) = Serving(Envelope(addressee, Encoding.UTF8.GetBytes("Opened with the key.")));
        NoKeys();
        Enrolled(("Office key", addressee));
        await preview.RenderAsync(APreview());
        Assert.True(preview.CanOpenSealed);

        EnvelopeOpener.Keys = () => [addressee];   // the key is presented
        await preview.OpenSealedCommand.ExecuteAsync(null);

        Assert.Null(preview.SealedState);
        Assert.Equal("Opened with the key.", preview.PreviewText);
        Assert.True(preview.ContentWasEnveloped);
    }

    [Fact]
    public async Task A_damaged_envelope_is_a_plain_error_in_the_pane_not_a_sealed_state()
    {
        var (preview, status) = Serving(Encoding.ASCII.GetBytes("not an envelope at all"));
        NoKeys();

        await preview.RenderAsync(APreview());

        Assert.Equal(Strings.Get("PvFailedDamaged"), preview.PreviewPlaceholder);
        Assert.Null(preview.SealedState);
        Assert.Empty(status.Reports);
    }

    [Fact]
    public async Task A_failed_read_says_so_in_the_pane_rather_than_loading_for_ever()
    {
        var (preview, _) = Serving([], HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<HttpRequestException>(() => preview.RenderAsync(APreview()));   // the load reports it, as before

        Assert.Equal(Strings.Get("PvFailedNetwork"), preview.PreviewPlaceholder);
    }

    [Fact]
    public async Task A_new_card_in_the_reader_checks_a_sealed_pane_again()
    {
        var addressee = NewCertificate();
        var (preview, _) = Serving(Envelope(addressee, Encoding.UTF8.GetBytes("Now readable.")));
        NoKeys();
        Enrolled(("Office key", addressee));
        await preview.RenderAsync(APreview());
        Assert.Equal(SealedContentState.Sealed, preview.SealedState);

        EnvelopeOpener.Keys = () => [addressee];   // the key changed
        await preview.RecheckSealedAsync();

        Assert.Equal("Now readable.", preview.PreviewText);
    }

    private static readonly System.Globalization.CultureInfo English = System.Globalization.CultureInfo.InvariantCulture;

    private static void Enrolled(params (string Label, X509Certificate2 Certificate)[] keys) =>
        EnvelopeOpener.SignedIn = _ => Task.FromResult(new EnrolledKeysAnswer([.. keys.Select(k => EnrolledKey.Of(k.Certificate, k.Label))]));

    // ---- fixture -----------------------------------------------------------------------------------------

    private static Preview APreview() => new($"{Installation}/api/documents/d/versions/v/enveloped-content?inline=true", false, null, null, null, ".txt");

    // Only the software opener, so the cases do not depend on this host's card reader.
    private static void NoKeys()
    {
        EnvelopeOpener.Openers = [EnvelopeOpener.Openers[0]];
        EnvelopeOpener.Keys = () => [];
        EnvelopeOpener.CardCertificatesSeen = () => [];
        EnvelopeOpener.PromptDeclined = () => false;
        EnvelopeOpener.SignedIn = EnvelopeOpener.NoSession;
    }

    private static (PreviewViewModel Preview, TestShell Status) Serving(byte[] body, HttpStatusCode code = HttpStatusCode.OK)
    {
        DesktopClientOptions.ApiBaseUrl = Installation;
        var status = new TestShell();
        var preview = new PreviewViewModel(status) { Api = new SimplArchiveApiClient("token") };
        ApiCore.Authenticated = new HttpClient(new Answering(body, code));   // after the client, which installs its own
        return (preview, status);
    }

    private sealed class Answering(byte[] body, HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pkcs7-mime");
            return Task.FromResult(new HttpResponseMessage(code) { Content = content });
        }
    }

    // Built the way the server builds one: MimeKit, CMS EnvelopedData, AES-256 (see DesktopEnvelopeOpenerTests).
    private static byte[] Envelope(X509Certificate2 recipient, byte[]? content = null)
    {
        var message = new MimeMessage
        {
            Subject = "sealed",
            Body = new MimePart(ContentType.Parse("text/plain"))
            {
                Content = new MimeContent(new MemoryStream(content ?? Encoding.UTF8.GetBytes("sealed text"))),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        using var context = new TemporarySecureMimeContext();
        message.Body = ApplicationPkcs7Mime.Encrypt(
            context,
            new CmsRecipientCollection { new CmsRecipient(recipient) { EncryptionAlgorithms = [EncryptionAlgorithm.Aes256] } },
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
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
