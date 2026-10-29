using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// One index field in an editor, once for the desktop, the web and the phone (ADR 0877). The desktop's and the web's
// copies had drifted on reading a Boolean; this pins the shared answer.
public class IndexFieldEditTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    [Theory]
    [InlineData("Date", false, false, IndexFieldEditor.Date)]
    [InlineData("DateTime", false, false, IndexFieldEditor.DateTime)]
    [InlineData("Boolean", false, false, IndexFieldEditor.Boolean)]
    [InlineData("Number", false, false, IndexFieldEditor.Text)]
    [InlineData("MultiSelect", false, false, IndexFieldEditor.Lines)]   // its type means a list
    [InlineData("Date", true, false, IndexFieldEditor.Lines)]           // a list of dates is a list first
    [InlineData("Date", true, true, IndexFieldEditor.ReadOnly)]         // locked beats everything
    public void The_editor_follows_lock_then_multiplicity_then_type(string dataType, bool isList, bool locked, IndexFieldEditor editor) =>
        Assert.Equal(editor, IndexFieldEdit.EditorFor(dataType, isList, locked));

    [Theory]
    [InlineData(true, false, false, true)]    // needs the routing right this caller lacks
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]    // the classifier owns it, for everybody
    public void A_field_is_locked_for_this_caller_or_for_everybody(bool needsRouting, bool mayRoute, bool classifierOwned, bool locked) =>
        Assert.Equal(locked, IndexFieldEdit.IsLocked(needsRouting, mayRoute, classifierOwned));

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]   // the web's copy read this as false
    [InlineData("false", false)]
    public void A_boolean_reads_regardless_of_case(string stored, bool flag) =>
        Assert.Equal(flag, IndexFieldEdit.Load(IndexFieldEditor.Boolean, "Boolean", [stored], Zurich).Flag);

    [Fact]
    public void A_read_only_field_shows_readable_values_but_echoes_the_stored_ones()
    {
        IReadOnlyList<string> stored = ["2026-10-12T08:30:00Z"];

        var shown = IndexFieldEdit.Load(IndexFieldEditor.ReadOnly, "DateTime", stored, Zurich);

        Assert.DoesNotContain("T08:30", shown.Text, StringComparison.Ordinal);
        Assert.Same(stored, IndexFieldEdit.Store(IndexFieldEditor.ReadOnly, shown with { Text = "edited" }, stored, Zurich));
    }

    [Fact]
    public void Lines_split_trimmed_and_without_blanks() =>
        Assert.Equal(["a@x", "b@x"], IndexFieldEdit.Store(IndexFieldEditor.Lines, new(" a@x \n\n b@x"), [], Zurich));

    [Fact]
    public void A_date_round_trips_as_its_wire_day()
    {
        var loaded = IndexFieldEdit.Load(IndexFieldEditor.Date, "Date", ["2026-03-31"], Zurich);

        Assert.Equal(["2026-03-31"], IndexFieldEdit.Store(IndexFieldEditor.Date, loaded, [], Zurich));
    }

    [Fact]
    public void A_moment_round_trips_through_the_viewers_zone()
    {
        var loaded = IndexFieldEdit.Load(IndexFieldEditor.DateTime, "DateTime", ["2026-10-12T08:30:00Z"], Zurich);

        Assert.Equal(TimeSpan.FromHours(10.5), loaded.Time);   // 08:30 UTC is 10:30 in Zurich (summer time)
        Assert.Equal(IndexInstant.Compose(loaded.Date, loaded.Time, Zurich), Assert.Single(IndexFieldEdit.Store(IndexFieldEditor.DateTime, loaded, [], Zurich)));
    }

    [Theory]
    [InlineData("  42 ", "42")]
    [InlineData("   ", null)]
    public void A_single_line_is_trimmed_and_an_empty_one_stores_nothing(string typed, string? stored) =>
        Assert.Equal(stored is null ? [] : [stored], IndexFieldEdit.Store(IndexFieldEditor.Text, new(typed), [], Zurich));
}
