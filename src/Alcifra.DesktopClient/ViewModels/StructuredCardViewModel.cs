using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>An editable copy and its save, which also refreshes this card.</summary>
    public sealed record StructuredEdit(StructuredEditFormViewModel Form, Func<StructuredEditFormViewModel, Task> SaveAsync);
}
