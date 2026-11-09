using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcifra.DesktopClient.ViewModels;

// A CI/CD secret in the preview pane (ADR 1084), its own partial beside the password entry's: shown from memory, and
// closed (nothing revealed, no capture protection held) whenever the preview moves on.
public sealed partial class PreviewViewModel
{
    /// <summary>The CI/CD secret on show; null when the preview shows anything else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private CiCdEntryPreviewViewModel? _ciCdEntry;

    /// <summary>Shows a CI/CD secret: its plain index at once, its values once opened with the key.</summary>
    public void ShowCiCdEntry(CiCdEntryPreviewViewModel entry)
    {
        Reset(null);
        LastRendered = null;
        (PreviewConverted, _annotationsUrl, AddNoteMode) = (false, null, false);
        CiCdEntry = entry;
        ContentWasEnveloped = true;
    }

    private void CloseCiCdEntry()
    {
        CiCdEntry?.Close();
        CiCdEntry = null;
    }
}
