using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// The New dialog's form for a mask with REQUIRED fields (#2217, with the decisions taken on Mobile #271): the name and
/// the mask's whole form, required fields marked, read from the definition the <c>admits</c> entry advertised. Nothing
/// exists until Create, and Create is offered only once every required field has a value, so the server's refusal is
/// not how a user learns which fields a kind needs. A mask without required fields keeps the name prompt (ADR 1067).
/// </summary>
/// <remarks>
/// The rows are the detail pane's own editors (<see cref="MaskFieldEditViewModel"/>), so a field edits here exactly as it
/// edits after the fact: one editor per data type, not two that disagree. The values travel in the create itself
/// (one request, ADR 0794), in the shape the index-data PUT takes.
/// </remarks>
public sealed partial class NewItemViewModel : ObservableObject
{
    /// <summary>The mask's name, as the menu entry read it: what the dialog is titled with.</summary>
    public required string Title { get; init; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanCreate))] private string _name = string.Empty;

    /// <summary>The mask's fields, in the definition's order, required ones marked by their label.</summary>
    public ObservableCollection<MaskFieldEditViewModel> Fields { get; } = [];

    /// <summary>A name, and a value in every required field.</summary>
    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(Name)
        && Fields.Where(f => f.IsRequired).All(f => f.ToValues().Any(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>What goes into the create beside the name: every field that has a value, in the index-data shape.</summary>
    public IReadOnlyList<(Guid FieldDefinitionId, IReadOnlyList<string> Values)> FieldValues() =>
        Fields.Select(f => (f.FieldDefinitionId, f.ToValues())).Where(f => f.Item2.Count > 0).ToList();

    /// <summary>
    /// The form for <paramref name="admitted"/>, or null where its mask has no required field, so the plain name prompt
    /// stays. The definition is read from the address the entry carries (<see cref="CreatableChild.MaskHref"/>), never
    /// composed; an entry from a server that advertises none gets the name prompt, as before.
    /// </summary>
    public static async Task<NewItemViewModel?> ForAsync(AlcifraApiClient api, CreatableChild admitted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(admitted);
        // The entry says whether anything is required (#2216), so a kind that needs nothing costs no request; a server from
        // before that names nothing (null), and the definition decides.
        if (admitted.MaskHref is not { Length: > 0 } maskHref || admitted.RequiredFields is { Count: 0 })
        {
            return null;
        }

        var definition = await api.Masks.GetMaskFieldsAsync(maskHref, cancellationToken);
        if (!definition.Any(f => f.IsRequired))
        {
            return null;
        }

        var form = new NewItemViewModel { Title = admitted.Name };
        foreach (var field in definition)
        {
            var editor = MaskFieldEditViewModel.Create(field, []);
            // What the field completes from (#1127), as in the detail pane: absent when the server offered no values rel.
            if (editor.ValuesHref is { } valuesHref)
            {
                editor.SuggestionSource = async (typed, token) => await api.Masks.FieldValuesAsync(valuesHref, typed, token);
            }

            editor.PropertyChanged += (_, _) => form.OnPropertyChanged(nameof(CanCreate));
            form.Fields.Add(editor);
        }

        return form;
    }
}
