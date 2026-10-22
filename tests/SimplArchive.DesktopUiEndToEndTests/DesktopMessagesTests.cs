using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;
using SimplArchive.Messenger.Client;
using SimplArchive.Messenger.Client.Display;

namespace SimplArchive.UiEndToEndTests;

// The desktop's read-only Messages tab (core #1813): its key opener and when it shows. The threads themselves are
// drawn by the Messenger's display rules, which its own repository tests.
[Collection(UiCollection.Name)]
public class DesktopMessagesTests(SelfHostedAppFixture app) : IDisposable
{
    private readonly Func<X509Certificate2Collection> _originalKeys = EnvelopeOpener.Keys;

    public void Dispose() => EnvelopeOpener.Keys = _originalKeys;

    [Fact]
    public async Task A_conversation_key_sealed_to_a_key_this_machine_holds_opens_with_it()
    {
        var reader = NewCertificate();
        EnvelopeOpener.Keys = () => [reader];
        var key = ConversationKeys.NewKey();
        using var publicOnly = X509CertificateLoader.LoadCertificate(reader.RawData);

        var opened = await new ConversationKeyOpener().OpenAsync(ConversationKeys.SealTo(key, [publicOnly]));

        Assert.Equal(key, opened);   // a bare CMS envelope: its content as it is, not a document
    }

    [Fact]
    public async Task A_key_sealed_to_somebody_else_is_refused_saying_what_is_missing()
    {
        EnvelopeOpener.Keys = () => [NewCertificate()];
        using var other = X509CertificateLoader.LoadCertificate(NewCertificate().RawData);

        await Assert.ThrowsAsync<EnvelopeNotOpenedException>(() => new ConversationKeyOpener().OpenAsync(ConversationKeys.SealTo(ConversationKeys.NewKey(), [other])));
    }

    [Fact]
    public async Task Without_the_Messenger_on_the_installation_the_tab_stays_hidden()
    {
        DesktopClientOptions.ApiBaseUrl = app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(app.BaseUrl));
        var tab = new MessagesTabViewModel(new TestShell(), () => api, NoLiveEvents.Instance);

        await tab.DiscoverAsync();

        Assert.False(tab.IsOffered);   // the test installation runs no Messenger module
        await tab.LoadAsync();         // and loading it does nothing rather than failing
        Assert.Empty(tab.Conversations);
    }

    // SimplArchiveMessenger #26: the tab hears "is typing" off the window's one live connection, the Messenger's events only.
    [Fact]
    public void The_window_hands_the_tab_only_the_Messengers_own_live_events()
    {
        var router = new SimplArchive.Presentation.ModuleLiveEventRouter();
        var heard = new List<(string Name, string Payload)>();
        using (new MessengerLiveEvents(router).Subscribe((name, payload) => heard.Add((name, payload))))
        {
            router.Dispatch(new SimplArchive.Presentation.ModuleLiveEvent("messenger", "typing", "{}"));
            router.Dispatch(new SimplArchive.Presentation.ModuleLiveEvent("flight-school", "typing", "{}"));
        }

        router.Dispatch(new SimplArchive.Presentation.ModuleLiveEvent("messenger", "typing", "{}"));   // let go of: not heard
        Assert.Equal([("typing", "{}")], heard);
    }

    // #1878: an attachment that would not open was reported as "The backup failed" and logged nowhere.
    [Fact]
    public async Task A_failed_act_is_worded_for_that_act_in_the_status_line()
    {
        var shell = new TestShell();
        var tab = new MessagesTabViewModel(shell, () => null, NoLiveEvents.Instance);

        await tab.Reported("MsgFileOpenFailed", () => throw new HttpRequestException("no key here"));
        await tab.Reported("MsgRestoreFailed", () => throw new IOException("disk full"));

        Assert.Equal(
            [$"{Strings.Get("MsgFileOpenFailed").Replace("{0}", "no key here")}", $"{Strings.Get("MsgRestoreFailed").Replace("{0}", "disk full")}"],
            shell.Reports);
        Assert.DoesNotContain(shell.Reports, r => r.StartsWith(Strings.Get("MobBackupFailed").Replace("{0}", string.Empty), StringComparison.Ordinal));
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=reader-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "t"), "t", X509KeyStorageFlags.Exportable);
    }
}
