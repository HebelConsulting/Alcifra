using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// The German manual is a translation of the English one, kept in step (ADR 1011, #2004): the same chapters, figures,
// references, features and index markers, in the same numbers. A section added to one and not the other fails here, and
// so does a cross-reference or a figure path the translation broke. The prose itself is a reviewer's to check; this
// guards the skeleton the two must share.
public partial class ManualTranslationParityTests
{
    public static TheoryData<string, string> Skeleton() => new()
    {
        { "labels", @"<[a-z][a-z0-9-]*>" },
        { "references", @"@[a-z][a-z0-9-]*" },
        { "figure paths", "\"screenshots/[^\"]+\"" },
        { "feature ids", "#feature\\(id: \"[^\"]+\"" },
        { "feature figures", "figure: \"[^\"]+\"" },
    };

    [Theory]
    [MemberData(nameof(Skeleton))]
    public void The_German_manual_carries_the_same_skeleton_as_the_English(string what, string pattern)
    {
        var (english, german) = Manuals();

        // Counted per value: a reference used three times in English and twice in German is a difference too.
        var counts = Sorted(english, pattern).Concat(Sorted(german, pattern)).Distinct()
            .Select(v => (Value: v, English: Sorted(english, pattern).Count(e => e == v), German: Sorted(german, pattern).Count(g => g == v)))
            .Where(c => c.English != c.German)
            .Select(c => $"{c.Value} (English {c.English}, German {c.German})")
            .ToList();

        Assert.True(counts.Count == 0,
            $"The manuals' {what} differ: {string.Join("; ", counts)}. Change both manual/manual.typ and manual/manual.de.typ together.");
    }

    [Theory]
    [InlineData("headings", @"(?m)^=+ ")]
    [InlineData("index markers", @"#idx\(")]
    [InlineData("notes", @"#note\[")]
    [InlineData("figures", @"#(?:shot|pair)\(")]
    [InlineData("code blocks", "```")]
    public void The_German_manual_has_as_many_of_each_element(string what, string pattern)
    {
        var (english, german) = Manuals();

        Assert.True(Regex.Count(english, pattern) == Regex.Count(german, pattern),
            $"The English manual has {Regex.Count(english, pattern)} {what}, the German one {Regex.Count(german, pattern)}. "
            + "Change both manual/manual.typ and manual/manual.de.typ together.");
    }

    [Fact]
    public void The_German_manual_is_Swiss_Standard_German() =>
        Assert.DoesNotContain('ß', Manuals().German);

    private static (string English, string German) Manuals() =>
        (File.ReadAllText(Path.Combine(RepoPaths.Root(), "manual", "manual.typ")),
         File.ReadAllText(Path.Combine(RepoPaths.Root(), "manual", "manual.de.typ")));

    private static List<string> Sorted(string text, string pattern) =>
        [.. Regex.Matches(text, pattern).Select(m => m.Value).Order(StringComparer.Ordinal)];
}
