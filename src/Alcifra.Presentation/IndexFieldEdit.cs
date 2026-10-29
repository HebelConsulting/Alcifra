namespace Alcifra.Presentation;

/// <summary>Which editor an index field gets.</summary>
public enum IndexFieldEditor
{
    /// <summary>Seen but not changed: the caller lacks the right (mail routing, #703) or the classifier owns it.</summary>
    ReadOnly,

    /// <summary>A list, one value per line: the field holds many values (#703), or its type means it (MultiSelect).</summary>
    Lines,

    Date,

    /// <summary>A moment, not a day (#660): a date AND a time, in the viewer's zone.</summary>
    DateTime,

    Boolean,

    /// <summary>Everything else on one line (Text, Number, SingleSelect, EmailAddress).</summary>
    Text,
}

/// <summary>What an editor holds while the user edits one field.</summary>
public sealed record IndexFieldValue(string Text, DateTime? Date = null, TimeSpan? Time = null, bool Flag = false);

/// <summary>
/// One index field in an editor, answered once for every client (ADR "Editable mask on the detail pane"; ADRs
/// 0511, 0877): which editor the field gets, what that editor shows of the stored values, and the wire values it
/// saves back. The desktop's and the web's field models held a copy each, and they had drifted (the web read only a
/// lower-case "true"); the phone's Details pencil (mobile #19) is the third user.
/// </summary>
public static class IndexFieldEdit
{
    /// <summary>Read-only for THIS caller (a field needing the mail-routing right they lack, #703) or for EVERY caller
    /// (the classifier owns the value, ADRs 0743/0744; its real write path is the content editor).</summary>
    public static bool IsLocked(bool requiresMailRouting, bool mayRouteMail, bool classifierOwned) =>
        (requiresMailRouting && !mayRouteMail) || classifierOwned;

    /// <summary>The editor for a field. Multiplicity is decided BEFORE the type: a list of dates is a list first, so it
    /// gets the list editor rather than a date picker that could only ever hold one of them.</summary>
    public static IndexFieldEditor EditorFor(string dataType, bool isList, bool locked) => (locked, isList || dataType == "MultiSelect", dataType) switch
    {
        (true, _, _) => IndexFieldEditor.ReadOnly,
        (_, true, _) => IndexFieldEditor.Lines,
        (_, _, "Date") => IndexFieldEditor.Date,
        (_, _, "DateTime") => IndexFieldEditor.DateTime,
        (_, _, "Boolean") => IndexFieldEditor.Boolean,
        _ => IndexFieldEditor.Text,
    };

    /// <summary>What the editor shows of the stored values. A read-only field is readable, not raw: a DateTime shows
    /// its wall clock in <paramref name="zone"/> instead of the ISO wire string.</summary>
    public static IndexFieldValue Load(IndexFieldEditor editor, string dataType, IReadOnlyList<string> values, TimeZoneInfo zone)
    {
        var first = values.Count > 0 ? values[0] : null;
        return editor switch
        {
            IndexFieldEditor.ReadOnly => new(string.Join(", ", values.Select(v => dataType == "DateTime" ? IndexInstant.Display(v, zone) : v))),
            IndexFieldEditor.Lines => new(string.Join("\n", values)),
            IndexFieldEditor.Date => new(string.Empty, System.DateTime.TryParse(first, out var day) ? day.Date : null),
            IndexFieldEditor.DateTime => IndexInstant.Split(first, zone) is var (date, time) ? new(string.Empty, date, time) : new(string.Empty),
            IndexFieldEditor.Boolean => new(string.Empty, Flag: string.Equals(first, "true", StringComparison.OrdinalIgnoreCase)),
            _ => new(first ?? string.Empty),
        };
    }

    /// <summary>
    /// The wire values the editor saves. A read-only field echoes <paramref name="stored"/> verbatim: the detail PUT is
    /// a full replacement, and the server's classifier-owned guard (rightly) reads any deviation, a formatted rendering
    /// included, as an attempted edit.
    /// </summary>
    public static IReadOnlyList<string> Store(IndexFieldEditor editor, IndexFieldValue value, IReadOnlyList<string> stored, TimeZoneInfo zone) => editor switch
    {
        IndexFieldEditor.ReadOnly => stored,
        IndexFieldEditor.Lines => value.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        IndexFieldEditor.Date => value.Date is { } day ? [day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)] : [],
        IndexFieldEditor.DateTime => IndexInstant.Compose(value.Date?.Date, value.Time, zone) is { } instant ? [instant] : [],
        IndexFieldEditor.Boolean => [value.Flag ? "true" : "false"],
        _ => string.IsNullOrWhiteSpace(value.Text) ? [] : [value.Text.Trim()],
    };
}
