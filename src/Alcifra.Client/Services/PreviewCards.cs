using System.Net.Http.Json;
using System.Text.Json;
using MudBlazor;
using Alcifra.Client.Models;
using Alcifra.Localization;

namespace Alcifra.Client.Services;

/// <summary>A contact's or an appointment's card in a preview pane (#2215): the editor's own form, read-only, and its pencil.</summary>
public sealed record StructuredCard(ContactCardForm? Contact, AppointmentForm? Appointment, bool CanEdit, Func<Task>? Edit)
{
    /// <summary>A mail's card instead (ADR 1073): read-only, no pencil.</summary>
    public MailCard? Mail { get; init; }

    /// <summary>Reads the stored vCard or iCalendar for the collapsed source section (#2274), through the item's
    /// <c>source</c> rel as the editor's disclosure does (#648); null where the server offers none.</summary>
    public Func<Task<string?>>? LoadSource { get; init; }

    /// <summary>The source section's heading: "vCard source" on a contact, "iCalendar source" on an appointment.</summary>
    public string SourceTitle => Strings.Get(Contact is not null ? "CardSourceVCard" : "CardSourceICalendar");
}

/// <summary>A mail as the web shows it: the envelope, the sanitised HTML with its inline images as data, the text, and the
/// attachments with their open and download addresses.</summary>
public sealed record MailCard(string Subject, string From, string To, string? Cc, DateTimeOffset? Date, string? Html, string Text,
    IReadOnlyList<MailAttachment> Attachments);

/// <summary>An attachment: its name and the presigned address it opens and downloads from (null where none is offered).</summary>
public sealed record MailAttachment(string Name, string? Url);

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
    private const string MailMaskName = "eMail";

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
            MailMaskName => await MailAsync(selfHref),
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
        })
        { LoadSource = Source(loaded.Links) };
    }

    // The source section (#2274) reads what the editor's "Advanced: the stored item" reads (#648): the structured resource's
    // own `source` rel. Where none is offered, the card has no section.
    private Func<Task<string?>>? Source(IReadOnlyDictionary<string, string> links) =>
        links.ContainsKey("source") ? async () => (await editors.ReadRawAsync(links))?.Text : null;

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
        })
        { LoadSource = Source(loaded.Links) };
    }

    // A mail's card (ADR 1073): the frame that shows its HTML is sandboxed and sends no credentials, so an inline image
    // is fetched here, with them, and handed over as data; the attachments carry their presigned download address.
    private async Task<StructuredCard?> MailAsync(string selfHref)
    {
        try
        {
            var document = await http.GetFromJsonAsync<JsonElement>(selfHref);
            if (Rel(document, "email-card") is not { } cardHref)
            {
                return null;
            }

            var card = await http.GetFromJsonAsync<JsonElement>(cardHref);
            var html = card.TryGetProperty("html", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            if (html is not null)
            {
                foreach (var src in System.Text.RegularExpressions.Regex.Matches(html, "src=\"([^\"]+/email-card/inline/\\d+)\"").Select(m => m.Groups[1].Value).Distinct().ToList())
                {
                    using var image = await http.GetAsync(src);
                    var data = image.IsSuccessStatusCode
                        ? $"data:{image.Content.Headers.ContentType?.MediaType};base64,{Convert.ToBase64String(await image.Content.ReadAsByteArrayAsync())}"
                        : string.Empty;
                    html = html.Replace($"src=\"{src}\"", $"src=\"{data}\"", StringComparison.Ordinal);
                }
            }

            var attachments = new List<MailAttachment>();
            foreach (var a in card.TryGetProperty("attachments", out var list) ? list.EnumerateArray().ToList() : [])
            {
                var name = a.GetProperty("name").GetString() ?? string.Empty;
                string? url = null;
                if (Rel(a, "versions") is { } versionsHref)
                {
                    var versions = await http.GetFromJsonAsync<VersionListResponse>(versionsHref);
                    var current = VersionListResponse.PickCurrent(versions?.Versions?.Where(v => v.Status == "Confirmed").ToList() ?? [], versions?.CurrentVersionId);
                    url = current is null ? null : http.Absolute(Hypermedia.Links.Href(current.Links, "download"));
                }

                attachments.Add(new MailAttachment(name, url));
            }

            string? Str(string property) => card.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new StructuredCard(null, null, false, null)
            {
                Mail = new MailCard(Str("subject") ?? string.Empty, Str("from") ?? string.Empty, Str("to") ?? string.Empty, Str("cc"),
                    card.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetDateTimeOffset() : null,
                    html, Str("text") ?? string.Empty, attachments),
            };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static string? Rel(JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == rel) is { ValueKind: JsonValueKind.Object } link
                ? link.GetProperty("href").GetString()
                : null
            : null;

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
