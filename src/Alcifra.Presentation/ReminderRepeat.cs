namespace Alcifra.Presentation;

/// <summary>
/// How often a reminder repeats, as the server stores it (0 none, 1 daily, 2 weekly, 3 monthly), and the label each
/// client shows: the appointment repeat labels, which say the same. Shared by the desktop's reminder dialog and the
/// phone's (mobile slice 5); the desktop listed them as hard-coded English until then.
/// </summary>
public static class ReminderRepeat
{
    /// <summary>The label keys, indexed by the stored value.</summary>
    public static IReadOnlyList<string> LabelKeys { get; } = ["RepeatNone", "RepeatDaily", "RepeatWeekly", "RepeatMonthly"];
}
