using System.Net.Http.Json;
using MudBlazor;
using Alcifra.Client.Dialogs;
using Alcifra.Client.Models;

namespace Alcifra.Client.Services;

/// <summary>
/// New ▸ a kind with REQUIRED fields (#2217, ADR 1067, the decisions of Mobile #271): reads the mask's definition from the
/// address the <c>admits</c> entry advertised, and where a field is required asks for the name and the mask's whole form
/// in one dialog, so the create carries the values the server would otherwise refuse it for. A kind without required
/// fields is none of this class's business: the plain name prompt stays. The desktop's form is the reference (ADR 0511).
/// </summary>
/// <remarks>
/// Its own class rather than more of <c>DocumentActions</c>, which stands at the 1000-line limit: the form's reading,
/// asking and shaping is one responsibility, and the actions keep only the call.
/// </remarks>
public sealed class NewItemCreation(HttpClient http, IDialogService dialogs)
{
    /// <summary>What a filled form posts: the body for the entry's address, and the name for the message.</summary>
    public sealed record Filled(object Payload, string Name);

    /// <summary>
    /// The form's fields for <paramref name="admitted"/>, or null where its mask has no required field. Read from the
    /// address the entry carries, never composed (ADR 0543); an entry from a server that advertises none gets null.
    /// </summary>
    public async Task<List<EditField>?> RequiredFieldFormAsync(CreatableChild admitted)
    {
        if (admitted.MaskHref is not { Length: > 0 } maskHref)
        {
            return null;
        }

        var definition = (await http.GetFromJsonAsync<MaskFieldsResponse>(maskHref))?.Fields ?? [];
        return definition.Any(f => f.IsRequired) ? [.. definition.Select(f => EditField.Create(f, []))] : null;
    }

    /// <summary>
    /// Opens the dialog over <paramref name="fields"/> and shapes what it hands back for the create: the values travel in
    /// the create itself (one request, ADR 0794), in the shape the index-data PUT takes. Null when the user cancels.
    /// </summary>
    public async Task<Filled?> AskAsync(
        CreatableChild admitted, List<EditField> fields,
        Func<string?, CancellationToken, Task<IReadOnlyList<NameSuggestion>>>? nameSuggestions)
    {
        // Named by their strings, as the dialogs' guard reads them (EditorRequiredDialogParameterTests).
        var parameters = new DialogParameters
        {
            { "Fields", fields },
            { "Suggest", (Func<string, string?, CancellationToken, Task<IEnumerable<string>>>)SuggestFieldValuesAsync },
        };
        if (nameSuggestions is not null)
        {
            parameters.Add("SuggestionSource", nameSuggestions);
        }

        var dialog = await dialogs.ShowAsync<NewItemDialog>(admitted.Name, parameters);
        if (await dialog.Result is not { Canceled: false, Data: NewItemDialog.NewItem filled })
        {
            return null;
        }

        var payload = new
        {
            name = filled.Name,
            maskId = admitted.MaskId,
            fields = filled.Fields.Select(f => new { fieldDefinitionId = f.FieldDefinitionId, values = f.ToValues() })
                .Where(f => f.values.Count > 0).ToList(),
        };
        return new Filled(payload, filled.Name);
    }

    // The values filed under a field, for its editor's completion (#1127): the pane's reading, repeated here because a
    // dialog owns no api client. Best-effort, as there: a list that cannot be read costs the completion, never the edit.
    private async Task<IEnumerable<string>> SuggestFieldValuesAsync(string valuesHref, string? typed, CancellationToken token)
    {
        var href = typed is { Length: > 0 } ? $"{valuesHref}{(valuesHref.Contains('?') ? "&" : "?")}q={Uri.EscapeDataString(typed)}" : valuesHref;
        try
        {
            return (await http.GetFromJsonAsync<FieldValues>(href, token))?.Values ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private sealed class FieldValues
    {
        public List<string> Values { get; set; } = [];
    }
}
