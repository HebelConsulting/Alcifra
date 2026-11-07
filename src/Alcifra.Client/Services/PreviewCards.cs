using System.Net.Http.Json;
using System.Text.Json;
using MudBlazor;
using Alcifra.Localization;

namespace Alcifra.Client.Services;

/// <summary>A contact's or an appointment's card in a preview pane (#2215): the editor's own form, read-only, and its pencil.</summary>
public sealed record StructuredCard(ContactCardForm? Contact, AppointmentForm? Appointment, bool CanEdit, Func<Task>? Edit);

/// <summary>
/// Decides whether a preview shows a card instead of a rendering, and builds it (#2215). Only a document whose mask is
/// Contact or Appointment gets one (owner, 2026-11-07), never by its file's extension, and only where the server offers the
/// matching rel (ADR 0543). The pencil opens the existing editor dialog; saving there shows the card again. The desktop's
/// MainWindowViewModel.Cards is its twin.
/// </summary>
public sealed class PreviewCards(HttpClient http, StructuredEditors editors, IDialogService dialogs, ISnackbar snackbar)
{
    private const string ContactMaskName = "Contact";
    private const string AppointmentMaskName = "Appointment";

    /// <summary>
    /// The card for the document at <paramref name="selfHref"/>, or null where it gets none. <paramref name="maskName"/> is
    /// the mask the caller already read, or null to read it here (a search hit carries none). <paramref name="show"/> puts a
    /// card on screen, again after a save.
    /// </summary>
    public async Task<StructuredCard?> ForAsync(string? selfHref, string? maskName, Func<StructuredCard, Task> show)
    {
        if (selfHref is null)
        {
            return null;
        }

        maskName ??= await MaskNameAsync(selfHref);
        return maskName switch
        {
            ContactMaskName => await ContactAsync(selfHref, show),
            AppointmentMaskName => await AppointmentAsync(selfHref, show),
            _ => null,
        };
    }

    private async Task<StructuredCard?> ContactAsync(string selfHref, Func<StructuredCard, Task> show)
    {
        if (await editors.ReadAsync(selfHref, "contact-card", ContactCardForm.From) is not { } loaded)
        {
            return null;
        }

        loaded.Value.CanEdit = false;
        return new StructuredCard(loaded.Value, null, loaded.CanEdit, async () =>
        {
            if (await editors.ReadAsync(selfHref, "contact-card", ContactCardForm.From) is not { } fresh)
            {
                return;
            }

            fresh.Value.CanEdit = fresh.CanEdit;
            var dialog = await dialogs.ShowAsync<Dialogs.ContactDialog>(Strings.Get("ContactEditTitle"),
                new DialogParameters { { nameof(Dialogs.ContactDialog.Form), fresh.Value } });
            if (await dialog.Result is { Canceled: false, Data: Dialogs.ContactDialog.Saved saved })
            {
                await SaveAndShowAsync(fresh.Href, saved.Form.ToPayload(), fresh.ETag, () => ContactAsync(selfHref, show), show);
            }
        });
    }

    private async Task<StructuredCard?> AppointmentAsync(string selfHref, Func<StructuredCard, Task> show)
    {
        if (await editors.ReadAsync(selfHref, "appointment", AppointmentForm.From) is not { } loaded)
        {
            return null;
        }

        loaded.Value.CanEdit = false;
        return new StructuredCard(null, loaded.Value, loaded.CanEdit, async () =>
        {
            if (await editors.ReadAsync(selfHref, "appointment", AppointmentForm.From) is not { } fresh)
            {
                return;
            }

            fresh.Value.CanEdit = fresh.CanEdit;
            var dialog = await dialogs.ShowAsync<Dialogs.AppointmentDialog>(Strings.Get("CalendarEdit"),
                new DialogParameters { { nameof(Dialogs.AppointmentDialog.Form), fresh.Value } });
            if (await dialog.Result is { Canceled: false, Data: Dialogs.AppointmentDialog.Saved saved })
            {
                await SaveAndShowAsync(fresh.Href, saved.Form.ToPayload(), fresh.ETag, () => AppointmentAsync(selfHref, show), show);
            }
        });
    }

    private async Task SaveAndShowAsync(string href, object payload, string etag, Func<Task<StructuredCard?>> reread, Func<StructuredCard, Task> show)
    {
        var outcome = await editors.SaveWithProblemAsync(href, payload, etag);
        if (outcome.Problem is { } problem)
        {
            snackbar.Add(problem, Severity.Error);
            return;
        }

        if (await reread() is { } card)
        {
            await show(card);
        }
    }

    // A search hit names no mask: its document's mask rel says (one read, only where a hit is previewed).
    private async Task<string?> MaskNameAsync(string selfHref)
    {
        try
        {
            var document = await http.GetFromJsonAsync<JsonElement>(selfHref);
            var maskHref = document.TryGetProperty("links", out var links)
                ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == "mask") is { ValueKind: JsonValueKind.Object } link
                    ? link.GetProperty("href").GetString()
                    : null
                : null;
            return maskHref is null ? null : (await http.GetFromJsonAsync<JsonElement>(maskHref)).TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }
}
