using System.Xml.Linq;

namespace Alcifra.UnitTests;

// The German strings are Swiss Standard German: "ss", never "ß" (owner, 2026-10-30, #2005). The website and the manual
// are written that way (#2004), and their German screenshots show these strings, so one "ß" here puts two spellings
// side by side on the same page. Germany and Austria read the Swiss spelling without trouble; the reverse mix reads
// as a mistake.
public class SwissGermanSpellingTests
{
    [Fact]
    public void Every_German_string_is_spelled_the_Swiss_way()
    {
        var resx = XDocument.Load(Path.Combine(RepoPaths.Root(), "src", "Alcifra.Localization", "Strings.de.resx"));
        var strings = resx.Root!.Elements("data")
            .Select(d => (Key: (string?)d.Attribute("name") ?? string.Empty, Value: (string?)d.Element("value") ?? string.Empty))
            .ToList();
        Assert.True(strings.Count > 100, $"Strings.de.resx yielded only {strings.Count} strings: this guard is reading the wrong file.");

        var german = strings.Where(s => s.Value.Contains('ß')).Select(s => $"{s.Key}: {s.Value}").ToList();

        Assert.True(german.Count == 0,
            "These German strings use \"ß\". Write \"ss\" (Swiss Standard German, #2005): \"Grösse\", \"schliessen\", "
            + "\"ausser\".\n" + string.Join("\n", german));
    }
}
