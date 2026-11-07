using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcifra.DesktopClient.ViewModels;

// A contact's or an appointment's card (#2215), its own partial as the password entry is: the fourth thing a preview shows
// beside pages, text and an entry, picked by the document's mask.
public sealed partial class PreviewViewModel
{
    /// <summary>The card on show; null when the preview shows anything else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(HasWatermark))]
    private StructuredCardViewModel? _card;

    /// <summary>A mail's card on show (#2215); null when the preview shows anything else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(HasWatermark))]
    private MailCardViewModel? _mailCard;

    /// <summary>Shows a mail's card in place of its rendition.</summary>
    public void ShowMailCard(MailCardViewModel card)
    {
        Reset(null);
        LastRendered = null;
        (PreviewConverted, _annotationsUrl, AddNoteMode) = (false, null, false);
        MailCard = card;
    }

    /// <summary>Shows <paramref name="card"/> in place of a rendering.</summary>
    public void ShowCard(StructuredCardViewModel card)
    {
        Reset(null);
        LastRendered = null;
        (PreviewConverted, _annotationsUrl, AddNoteMode) = (false, null, false);
        Card = card;
    }
}
