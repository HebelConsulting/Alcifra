using Alcifra.Presentation;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.ViewModels;

// A mask choice in the mask-change dropdown (ADR "Editable mask on the detail pane"). MaskId null = "(No mask)".
// Mask is the catalogue row the server sent, carried so reading the mask's fields follows the address it
// advertised (ADR 0543/0555). Null for the "(No mask)" entry and for the designer-preview rows, which reach
// no server — every real, selectable mask has it.
public sealed record MaskChoiceViewModel(Guid? MaskId, string Name, Alcifra.ApiClient.MasksClient.MaskOptionInfo? Mask = null)
{
    public override string ToString() => DisplayName;

    /// <summary>The name as a person reads it — a well-known one translated (ADR 0891); <see cref="Name"/> stays stored.</summary>
    public string DisplayName => Alcifra.Localization.WellKnownLabels.For(Name);
}

// One editable index field in the mask edit mode — renders a type-aware editor bound to the field's DataType
// (ADR "Editable mask on the detail pane"): a date picker for Date, a checkbox for Boolean, a multi-line box
// for a LIST (one value per line), else a single-line text box (Text / Number / SingleSelect / EmailAddress).
//
// The web client's EditField is the same shape and must stay it (ADR 0511) — one surface, two renderings.
public sealed partial class MaskFieldEditViewModel : ObservableObject
{
    public required Guid FieldDefinitionId { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsRequired { get; init; }

    /// <summary>Whether this field holds many values (#703).</summary>
    public bool IsList { get; init; }

    /// <summary>The caller may see this field but not change it (#703) — a Mailbox's address list for a
    /// caller without the routing right. Rendered read-only, so the refusal happens here instead of on save.</summary>
    public bool Locked { get; init; }

    /// <summary>
    /// The address of the values already filed under this field, when the server offers them (#1127).
    /// </summary>
    /// <remarks>Absent means "no suggestions here" — the editor stays a plain text box (ADR 0543).</remarks>
    public string? ValuesHref { get; init; }

    /// <summary>
    /// Fetches the suggestions for what has been typed so far. Assigned by whoever builds the field, because
    /// this view-model owns no api client.
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>>? SuggestionSource { get; set; }

    /// <summary>Whether this field completes as you type — a single-line editor WITH an offered value list.</summary>
    /// <remarks>
    /// Reported from use of a module's Aerodrome field, and true of its METAR/TAF and NOTAM siblings: every
    /// textual field rendered as a bare text box, so a vocabulary the tenant already uses had to be retyped
    /// from memory. Not for the multi-line list editor, where a line is one of many and a dropdown over the
    /// caret would fight the editing.
    /// </remarks>
    public bool Completes => IsSingleLine && !Locked && SuggestionSource is not null;

    /// <summary>The plain text box, for a single-line field with nothing to complete from.</summary>
    public bool IsPlainSingleLine => IsSingleLine && !Completes;

    [ObservableProperty] private string _textValue = string.Empty;
    // DateTime?, NOT DateTimeOffset? — Avalonia ships TWO date controls with DIFFERENT property types, and
    // this one binds a CalendarDatePicker, whose SelectedDate is DateTime? (DatePicker's is DateTimeOffset?).
    // The mismatch does not fail the build or any C# test: it fails at BINDING time with "Could not cast
    // DateTimeOffset to DateTime?", leaving every Date and DateTime mask field silently uneditable — the
    // owner met it on the Enrollment mask. Every other date-bearing view-model here is DateTime? for exactly
    // this reason; this one was the outlier. (Same family as the NumericUpDown/decimal? trap.)
    [ObservableProperty] private System.DateTime? _dateValue;
    [ObservableProperty] private System.TimeSpan? _timeValue;
    [ObservableProperty] private bool _boolValue;

    /// <summary>The stored wire values, kept verbatim so a LOCKED field round-trips unchanged: the pane
    /// shows a readable rendering, but the full-replacement PUT must echo exactly what the server holds —
    /// the classifier-owned guard refuses anything else (ADR 0744-era pane fix).</summary>
    private IReadOnlyList<string> _originalValues = [];

    /// <summary>Which editor this field gets: the one shared rule (<see cref="IndexFieldEdit"/>), which the web and the
    /// phone ask too.</summary>
    public IndexFieldEditor Editor => IndexFieldEdit.EditorFor(DataType, IsList, Locked);

    // The field's TYPE, kept as it was: a list of dates is still a Date field, whose editor is nonetheless the list.
    public bool IsDate => DataType == "Date" && !Locked;
    public bool IsDateTime => DataType == "DateTime" && !Locked;
    public bool IsBoolean => DataType == "Boolean" && !Locked;
    public bool IsMultiLine => Editor == IndexFieldEditor.Lines;
    public bool IsSingleLine => Editor is IndexFieldEditor.ReadOnly or IndexFieldEditor.Text;
    public string Label => IsRequired ? $"{Name} *" : Name;

    /// <summary>What a person reads beside the editor — a well-known field name translated (ADR 0891). <see cref="Label"/>
    /// stays the stored name, because proposals are keyed on it.</summary>
    public string DisplayLabel => IsRequired
        ? $"{Alcifra.Localization.WellKnownLabels.For(Name)} *"
        : Alcifra.Localization.WellKnownLabels.For(Name);

    // ---- Machine proposals (ABI 0.11, ADR 0769) --------------------------------------------------------
    // A picker beside this field's editor, offered when a machine proposal fills it. Typing stays primary
    // (the typed-not-picked rule); each choice carries its own apply command so the flyout's menu items
    // need no cross-tree binding back to this view model.

    public ObservableCollection<ProposalChoice> Proposals { get; } = [];

    [ObservableProperty] private bool _hasProposals;

    [ObservableProperty] private string _proposalLabel = string.Empty;

    public sealed record ProposalChoice(string Display, System.Windows.Input.ICommand ApplyCommand, string Value);

    public void OfferProposals(string label, IEnumerable<(string Value, string Label, string? Detail)> items)
    {
        Proposals.Clear();
        foreach (var (value, itemLabel, detail) in items)
        {
            var display = string.IsNullOrEmpty(detail) ? itemLabel : $"{itemLabel} — {detail}";
            Proposals.Add(new ProposalChoice(display,
                new CommunityToolkit.Mvvm.Input.RelayCommand(() => TextValue = value), value));
        }

        ProposalLabel = label;
        HasProposals = Proposals.Count > 0;
    }

    public static MaskFieldEditViewModel Create(MasksClient.MaskFieldInfo definition, IReadOnlyList<string> values, bool mayRouteMail = true)
    {
        var field = new MaskFieldEditViewModel
        {
            FieldDefinitionId = definition.Id,
            Name = definition.Name,
            DataType = definition.DataType,
            IsRequired = definition.IsRequired,
            IsList = definition.IsList,
            Locked = IndexFieldEdit.IsLocked(definition.RequiresMailRouting, mayRouteMail, definition.ClassifierOwned),
            ValuesHref = definition.ValuesHref,
        };
        field._originalValues = [.. values];
        var loaded = IndexFieldEdit.Load(field.Editor, definition.DataType, values, SessionTimeZone.Current);
        (field.TextValue, field.DateValue, field.TimeValue, field.BoolValue) = (loaded.Text, loaded.Date, loaded.Time, loaded.Flag);
        return field;
    }

    public IReadOnlyList<string> ToValues() =>
        IndexFieldEdit.Store(Editor, new IndexFieldValue(TextValue, DateValue, TimeValue, BoolValue), _originalValues, SessionTimeZone.Current);
}
