using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.ViewModels;

// A contact's or an appointment's card in the preview pane (#2215).
public partial class MainWindowViewModel
{
    // The masks a card is shown for (owner, 2026-11-07): a Contact and an Appointment, nothing else. A Booking is edited by
    // the same form and the server offers it the appointment rel, but its preview stays what it was.
    private const string ContactMaskName = "Contact";
    private const string AppointmentMaskName = "Appointment";

    /// <summary>
    /// Shows the open document as a card where its mask is Contact or Appointment, and the server offers the matching
    /// <c>contact-card</c> or <c>appointment</c> rel (ADR 0543). Never by the file's extension: a .vcf filed as a Basic
    /// Entry keeps its own preview. False where the document is neither, or its card cannot be read.
    /// </summary>
    private async Task<bool> TryShowStructuredCardAsync()
    {
        if (_api is null || _detailLinks?.Href("self") is not { } self)
        {
            return false;
        }

        var documentId = _selectedDocumentId;
        var mask = SelectedItem is { IsFolder: false } selected && selected.Id == documentId ? selected.DocumentType : null;
        StructuredCardViewModel? card = null;
        if (mask == ContactMaskName && _detailLinks.Href("contact-card") is not null)
        {
            card = await CardAsync(self, "contact-card", ContactEditViewModel.From);
        }
        else if (mask == AppointmentMaskName && _detailLinks.Href("appointment") is not null)
        {
            card = await CardAsync(self, "appointment", AppointmentEditViewModel.From);
        }

        if (card is null || _selectedDocumentId != documentId)
        {
            return false;
        }

        Preview.ShowCard(card);
        return true;
    }

    // One reading of the item, the card built over it, and its pencil: a fresh read for the editor (a card read minutes
    // ago may be stale), saved under its own ETag, then the card read again so it shows what was stored.
    private async Task<StructuredCardViewModel?> CardAsync<T>(string self, string rel, Func<System.Text.Json.JsonElement, T> parse)
        where T : StructuredEditFormViewModel
    {
        if (await _api!.StructuredEditors.ReadAsync(self, rel, parse) is not { } loaded)
        {
            return null;
        }

        return new StructuredCardViewModel(loaded.Value, loaded.CanEdit)
        {
            OpenForEdit = async () => await _api.StructuredEditors.ReadAsync(self, rel, parse) is { } fresh
                ? new StructuredCardViewModel.StructuredEdit(fresh.Value, async edited =>
                {
                    try
                    {
                        await _api.StructuredEditors.SaveAsync(fresh.Href, edited.ToPayload(), fresh.ETag);
                        if (await CardAsync(self, rel, parse) is { } shown && _detailLinks?.Href("self") == self)
                        {
                            Preview.ShowCard(shown);
                        }
                    }
                    catch (Exception e) when (e is ApiActionException or HttpRequestException)
                    {
                        Status = e.Message;
                    }
                })
                : null,
        };
    }
}
