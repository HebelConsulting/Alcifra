using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Passwords tab's wiring (ADR 0968), in its own partial (MainWindowViewModel.cs is on the over-limit debt list and
/// may only shrink): built on first use with the window's secret-field services, discovered at every sign-in, closed when
/// the key leaves; and the Repositories tree's half — a Password Entry met as a document shows its fields read-only in
/// the preview, opened with the key on every read.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The Passwords tab's index: appended after Messages, so no other tab is re-indexed.</summary>
    public const int PasswordsTabIndex = 16;

    private PasswordsTabViewModel? _passwordsTab;
    private SecretFieldServices? _secretServices;

    public PasswordsTabViewModel PasswordsTab => _passwordsTab ??= new PasswordsTabViewModel(this, () => _api, SecretServices);

    /// <summary>
    /// What a secret field needs from this window: the sensitive and the plain clipboard, the status line, and the screen
    /// protection — this window kept out of capture while a secret is shown, counted across the tab and the tree.
    /// </summary>
    internal SecretFieldServices SecretServices => _secretServices ??= new SecretFieldServices(
        new DesktopSecretClipboard(() => MainWindowOf()?.Clipboard),
        async text =>
        {
            if (MainWindowOf()?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            }
        },
        this,
        new SecretScreens(on => CaptureProtection.Apply(MainWindowOf(), on)));

    private static Window? MainWindowOf() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// The selected document is a Password Entry (its mask, ADR 0964): its preview is the entry's fields, read-only, opened
    /// with the hardware key — never the sealed bytes, which no renderer can show.
    /// </summary>
    private bool DetailIsPasswordEntry() =>
        SelectedItem is { IsFolder: false } selected && selected.Id == _selectedDocumentId && PasswordEntryDocument.Is(selected.DocumentType);

    /// <summary>
    /// Reads the entry's current version through the content funnel and opens it with the key, per read (Mobile ADR 0037's
    /// <c>PasswordEntryDocument</c>); shown read-only: changing it is the Passwords tab's. A cancelled prompt or a key not
    /// at hand says so in the placeholder.
    /// </summary>
    private async Task ShowPasswordEntryDocumentAsync(string versionsHref)
    {
        var (documentId, name) = (_selectedDocumentId, SelectedItem?.Name ?? string.Empty);
        Preview.Reset(Strings.Get("PwOpening"));
        try
        {
            var preview = await _api!.Documents.GetPreviewAsync(versionsHref);
            if (preview.DownloadUrl is not { } download)
            {
                throw new PasswordEntryUnreadableException("it has no content yet");
            }

            var (bytes, _, wasEnveloped) = await SimplArchiveApiClient.DownloadDetailedAsync(download);
            PasswordKeyOpener.UserAsked();
            var content = await PasswordEntryDocument.ParseAsync(bytes, wasEnveloped, PasswordKeyOpener.Instance);
            if (_selectedDocumentId == documentId)
            {
                Preview.ShowPasswordEntry(new PasswordEntryPreviewViewModel(name, content, null, SecretServices, isReadOnly: true));
            }
        }
        catch (Exception e) when (PasswordsTabViewModel.IsReported(e) && _selectedDocumentId == documentId)
        {
            Preview.Reset(PasswordWork.Describe(e) ?? Strings.Get("PwOpenToRead"));
        }
    }

    // A session's client for the Passwords tab: discovered again at every sign-in, so it never keeps an ended one's.
    private void UsePasswordsSession()
    {
        if (IsLoggedIn && _passwordsTab is { } passwords)
        {
            Safe.Fire(async () =>
            {
                await passwords.DiscoverAsync();
                if (SelectedTab == PasswordsTabIndex)
                {
                    await passwords.LoadAsync();
                }
            });
        }
    }
}
