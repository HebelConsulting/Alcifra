using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// An opened password entry in the preview pane (ADR 0968), its own partial: the third thing a preview shows beside pages
// and text, rendered from memory (nothing written anywhere, no server asked) and found in by the same find box — over the
// entry's VISIBLE text only, never a secret's value.
public sealed partial class PreviewViewModel
{
    /// <summary>The opened entry on show; null when the preview shows pages, text or nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(HasWatermark))]
    private PasswordEntryPreviewViewModel? _passwordEntry;

    /// <summary>
    /// Shows an opened entry. It counts as content the key opened, so the key leaving discards it — and, unlike a
    /// document, nothing brings it back by itself when the key returns: it is opened again with the key.
    /// </summary>
    public void ShowPasswordEntry(PasswordEntryPreviewViewModel entry)
    {
        Reset(null);
        LastRendered = null;
        (PreviewConverted, _annotationsUrl, AddNoteMode) = (false, null, false);
        PasswordEntry = entry;
        CanFindInDocument = true;
        ContentWasEnveloped = true;
        ApplyFindToEntry();
    }

    /// <summary>The window lost focus: every secret held open is hidden.</summary>
    public void HideSecrets() => PasswordEntry?.HideAll();

    private void ApplyFindToEntry()
    {
        FindCount = PasswordEntry?.ApplyFind(FindQuery) ?? 0;
        FindIndex = FindCount > 0 ? 0 : -1;
        PasswordEntry?.Activate(FindIndex);
    }

    // Reset's share: the entry is closed (nothing stays revealed, the capture protection lifts) and let go of.
    private void ClosePasswordEntry()
    {
        PasswordEntry?.Close();
        PasswordEntry = null;
    }
}
