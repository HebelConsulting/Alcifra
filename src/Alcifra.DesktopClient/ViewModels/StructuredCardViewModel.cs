using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A contact's or an appointment's card in the preview pane (#2215): the editor's own form in its read-only posture, chosen
/// by the document's mask, never by its file's extension. Its pencil opens the editor window the Contacts and Calendar tabs
/// use; the card owns no api client, so the reading and the saving are the builder's.
/// </summary>
public sealed partial class StructuredCardViewModel : ObservableObject
{
    /// <summary>The form on show, read-only: a <see cref="ContactEditViewModel"/> or an <see cref="AppointmentEditViewModel"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Contact))]
    [NotifyPropertyChangedFor(nameof(Appointment))]
    private StructuredEditFormViewModel _form;

    public StructuredCardViewModel(StructuredEditFormViewModel form, bool canEdit)
    {
        form.CanEdit = false;
        _form = form;
        CanEdit = canEdit;
    }

    public ContactEditViewModel? Contact => Form as ContactEditViewModel;

    public AppointmentEditViewModel? Appointment => Form as AppointmentEditViewModel;

    /// <summary>Whether the pencil is offered: the server lets this caller save the item.</summary>
    public bool CanEdit { get; }

    /// <summary>A fresh, editable copy of the item for the editor window, with what saving it takes; null when it cannot be read.</summary>
    public Func<Task<StructuredEdit?>>? OpenForEdit { get; init; }

    /// <summary>The source section's heading: "vCard source" on a contact, "iCalendar source" on an appointment (#2274).</summary>
    public string SourceTitle => Strings.Get(Contact is not null ? "CardSourceVCard" : "CardSourceICalendar");

    /// <summary>Reads the item's stored text through its <c>source</c> rel, as the editor's disclosure does (#648); null where
    /// the server offers none.</summary>
    public Func<Task<string?>>? LoadSource { get; init; }

    /// <summary>Whether the card offers its source section at all.</summary>
    public bool HasSource => LoadSource is not null;

    /// <summary>The section, collapsed until opened; the first opening reads the stored text, and only that one.</summary>
    [ObservableProperty]
    private bool _isSourceOpen;

    /// <summary>What the section shows: the stored text, a long photo shortened (<see cref="StructuredItemSource"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopySource))]
    private string? _sourceShown;

    /// <summary>The stored text itself, which Copy copies: never the shortened one.</summary>
    public string? Source { get; private set; }

    public bool CanCopySource => Source is not null;

    partial void OnIsSourceOpenChanged(bool value)
    {
        if (value && SourceShown is null && LoadSource is { } load)
        {
            Safe.Fire(async () =>
            {
                string? text;
                try
                {
                    text = await load();
                }
                catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
                                          or Alcifra.ApiClient.ApiActionException or System.Security.Cryptography.CryptographicException)
                {
                    text = null;
                }

                Source = text;
                SourceShown = text is null ? Strings.Get("CardSourceUnreadable") : StructuredItemSource.Shown(text);
            });
        }
    }

    /// <summary>An editable copy and its save, which also refreshes this card.</summary>
    public sealed record StructuredEdit(StructuredEditFormViewModel Form, Func<StructuredEditFormViewModel, Task> SaveAsync);
}
