using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A mail's card in the preview pane (#2215, ADR 1073): the envelope, the body as text (this client has no HTML view; #2243
/// follows), and the attachments, each opened in its own application or saved. Read-only: a mail is not edited.
/// </summary>
public sealed class MailCardViewModel(DocumentsClient.MailCard card, IReadOnlyList<MailAttachmentViewModel> attachments)
{
    public string Subject => card.Subject;

    public string From => card.From;

    public string To => card.To;

    public string? Cc => card.Cc;

    public bool HasCc => !string.IsNullOrEmpty(card.Cc);

    // The viewer's zone, not the device's (ADR 0801/0812), as every other instant on screen.
    public string Date => card.Date?.InZone(SessionTimeZone.Current).ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    public string Body => card.Text;

    public IReadOnlyList<MailAttachmentViewModel> Attachments => attachments;

    public bool HasAttachments => attachments.Count > 0;
}

/// <summary>One attachment of a mail card: its name, Open (its own application) and Save (to a file the person chooses).</summary>
public sealed partial class MailAttachmentViewModel(string name, Func<Task> open, Func<Task<(byte[] Bytes, string FileName)?>> read)
{
    public string Name => name;

    /// <summary>Reads the attachment's bytes and the file name it is saved under; the save dialog is the view's.</summary>
    public Func<Task<(byte[] Bytes, string FileName)?>> Read => read;

    [RelayCommand]
    private Task OpenAsync() => open();
}
