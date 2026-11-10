using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.Client.Services;

/// <summary>
/// A field's value completing from an active module's vocabulary (core #2288): the items for what was typed, and what a
/// stored code names. The desktop's FieldCompletionWiring is its twin. Followed from the field's <c>vocabulary</c> rel, never
/// composed (ADR 0543); best-effort, as the filed values are: a vocabulary that cannot be read costs the completion, never
/// the edit.
/// </summary>
public sealed class FieldVocabulary(HttpClient http)
{
    /// <summary>One item: the stored <paramref name="Value"/>, and what it names.</summary>
    public sealed record Item(string Value, string Label);

    public async Task<IReadOnlyList<Item>> SearchAsync(string vocabularyHref, string? typed, CancellationToken cancellationToken = default)
    {
        var href = $"{vocabularyHref}{(vocabularyHref.Contains('?') ? "&" : "?")}q={Uri.EscapeDataString(typed ?? string.Empty)}";
        try
        {
            var answer = await http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
            return answer.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? [.. items.EnumerateArray()
                    .Select(i => new Item(
                        i.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                        i.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty))
                    .Where(i => i.Value.Length > 0)]
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    /// <summary>What <paramref name="value"/> names in the vocabulary, or null where it holds no item with exactly that value.</summary>
    public async Task<string?> LabelAsync(string vocabularyHref, string? value, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : (await SearchAsync(vocabularyHref, value.Trim(), cancellationToken))
                .FirstOrDefault(i => string.Equals(i.Value, value.Trim(), StringComparison.OrdinalIgnoreCase))?.Label;
}
