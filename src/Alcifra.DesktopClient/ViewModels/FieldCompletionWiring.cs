using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// Gives a textual field what it completes from, which the field itself cannot fetch: the values already filed under it
/// (#1127) and, where an active module offers one, that module's vocabulary (core #2288). One place, so the detail pane and
/// the New dialog complete alike.
/// </summary>
internal static class FieldCompletionWiring
{
    public static void Wire(MaskFieldEditViewModel editor, AlcifraApiClient api)
    {
        var valuesHref = editor.ValuesHref;
        var vocabularyHref = editor.VocabularyHref;
        if (valuesHref is null && vocabularyHref is null)
        {
            return;
        }

        // The vocabulary first, each item naming what its code means; then what was filed that it does not hold, because
        // completion never validates and a value typed freely last month is still worth offering.
        editor.SuggestionSource = async (typed, cancellationToken) =>
        {
            var offered = vocabularyHref is null
                ? []
                : (await api.Masks.FieldVocabularyAsync(vocabularyHref, typed, cancellationToken))
                    .Select(i => new FieldSuggestion(i.Value, i.Label)).ToList();
            if (valuesHref is not null)
            {
                offered.AddRange((await api.Masks.FieldValuesAsync(valuesHref, typed, cancellationToken))
                    .Where(v => !offered.Any(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => new FieldSuggestion(v, null)));
            }

            return offered;
        };

        if (vocabularyHref is not null)
        {
            editor.LabelSource = async (value, cancellationToken) =>
                (await api.Masks.VocabularyLabelAsync(vocabularyHref, value, cancellationToken))?.Label;
            Safe.Fire(() => editor.RefreshValueLabelAsync());
        }
    }
}

/// <summary>One offered value in a field's type-ahead, with what a vocabulary says it names (core #2288).</summary>
/// <remarks><see cref="ToString"/> is what <c>AutoCompleteBox</c> puts in the box when an item is chosen: the code.</remarks>
public sealed record FieldSuggestion(string Value, string? Label)
{
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    public override string ToString() => Value;
}
