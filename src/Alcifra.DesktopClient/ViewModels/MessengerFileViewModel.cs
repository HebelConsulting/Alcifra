using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Messenger.Client;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A sent file, opened with the reader's key and shown IN THE APP from memory (ADR 0967, the phone's #27): a photo, a
/// PDF's pages or a plain text in the desktop's own preview, with find and zoom. Nothing of it is written anywhere. What
/// the app cannot show — an office document, and video or sound, for which no player with a compatible licence exists
/// for the desktop — can only be handed on deliberately, by <b>Open with…</b> or <b>Save as…</b> after a warning, and not
/// at all where the tenant keeps files in the app (<see cref="FileOpening.MayLeaveTheApp"/>).
/// </summary>
public sealed partial class MessengerFileViewModel : ObservableObject
{
    private readonly MessengerDialogs _dialogs;
    private readonly IStatusReporter _status;
    private readonly Action _close;
    private byte[] _content;

    private MessengerFileViewModel(byte[] content, SentFile file, FileOpening opening, MessengerDialogs dialogs, IStatusReporter status, Action close)
    {
        (_content, Name, Opening, _dialogs, _status, _close) = (content, file.Name, opening, dialogs, status, close);
        Preview = new PreviewViewModel(status);
    }

    /// <summary>Opens <paramref name="content"/> where the Messenger's rules say it is shown.</summary>
    public static async Task<MessengerFileViewModel> OpenAsync(
        byte[] content, SentFile file, bool filesStayInApp, MessengerDialogs dialogs, IStatusReporter status, Action close)
    {
        var opening = FileOpening.Of(content, file.Name, file.ContentType, filesStayInApp);
        var opened = new MessengerFileViewModel(content, file, opening, dialogs, status, close);
        if (opening.ShownIn is FileShownIn.Photo or FileShownIn.Preview)
        {
            try
            {
                opened.Shown = await opened.Preview.ShowContentAsync(content, opening.ContentType);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // A photo format the renderer does not decode (one the phone does): said, and handed on only on request.
                DesktopLog.Debug("Messages: an opened {ContentType} could not be drawn: {Reason}", opening.ContentType, e.Message);
            }
        }

        return opened;
    }

    public string Name { get; }

    public FileOpening Opening { get; }

    /// <summary>The desktop's own preview, holding the decrypted bytes in memory only.</summary>
    public PreviewViewModel Preview { get; }

    /// <summary>Whether the preview shows the file; otherwise the pane says why it does not.</summary>
    [ObservableProperty] private bool _shown;

    /// <summary>Why the app does not show it, and whether it may be handed on.</summary>
    public string NotShownLine => Opening.MayLeaveTheApp ? Strings.Get("MsgFileCannotShow") : ViewText.Get("KeptInApp");

    /// <summary>Open with… and Save as…: never where the tenant keeps files in the app.</summary>
    public bool MayLeaveTheApp => Opening.MayLeaveTheApp;

    /// <summary>
    /// Hands the file to the application the system opens it with — a plaintext copy the app no longer protects, so only
    /// after the warning says so, and never where the tenant keeps files in the app.
    /// </summary>
    [RelayCommand]
    private async Task OpenWithAsync()
    {
        if (!MayLeaveTheApp || !await WarnedAsync())
        {
            return;
        }

        try
        {
            await NativeFileOpener.OpenBytesAsync(_content, Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgFileOpenFailed"), e.Message));
        }
    }

    /// <summary>Saves a readable copy where the user chooses — after the same warning, and on the same condition.</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (!MayLeaveTheApp || _dialogs.SaveFile is not { } save || !await WarnedAsync() || await save(Name) is not { } stream)
        {
            return;
        }

        try
        {
            await using (stream)
            {
                await stream.WriteAsync(_content);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Report(string.Format(CultureInfo.CurrentCulture, Strings.Get("MsgFileOpenFailed"), e.Message));
        }
    }

    private Task<bool> WarnedAsync() =>
        _dialogs.ConfirmAsync(ViewText.Get("HandOnTitle"), ViewText.Get("HandOnWarning"), ViewText.Get("Continue"));

    /// <summary>Back to the conversation (✕ or Esc): what was drawn is let go of.</summary>
    [RelayCommand]
    public void Close() => _close();

    /// <summary>Lets go of the bytes and of what was drawn from them.</summary>
    public void Discard()
    {
        Array.Clear(_content);
        _content = [];
        Preview.Reset(null);
        Shown = false;
    }
}
