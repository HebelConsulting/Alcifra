using System.Globalization;
using System.Text.RegularExpressions;
using SimplArchive.Localization;
using SimplArchive.Theming;

namespace SimplArchive.UnitTests;

// The product's name is ONE value (core #1117, Mobile #187): every text that names the product says {Product}, and
// Strings fills in the name a host set, the brand's by default. A literal name in a text would survive a rename, or a change
// of the wordmark's casing, unnoticed.
public class ProductNameTests
{
    // Stored names and identifiers that keep their spelling: the manuals folder and the documentation mask are seeded
    // data that a text must name as they are, and the webhook's header is a protocol identifier.
    private static readonly HashSet<string> NamesStoredData = ["TenantWebhookUrlInfo"];

    private static readonly Regex Entry = new(@"<data name=""([^""]+)""[^>]*>\s*<value>(.*?)</value>", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void A_text_names_the_product_by_default_as_the_brand() =>
        Assert.Equal($"About {Brand.Shipped.Name}", Strings.Get("AboutHeading", CultureInfo.InvariantCulture));

    [Fact]
    public void A_host_sets_the_name_once_and_every_text_says_it()
    {
        try
        {
            Strings.ProductName = "Example Archive";
            Assert.Equal("About Example Archive", Strings.Get("AboutHeading", CultureInfo.InvariantCulture));
            Assert.Equal("Über Example Archive", Strings.Get("AboutHeading", CultureInfo.GetCultureInfo("de")));
        }
        finally
        {
            Strings.ProductName = Strings.DefaultProductName;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("es")]
    public void No_text_spells_the_product_name_out(string language)
    {
        var file = Path.Combine(RepoPaths.Root(), "src", "SimplArchive.Localization", language.Length == 0 ? "Strings.resx" : $"Strings.{language}.resx");
        var literal = Entry.Matches(File.ReadAllText(file))
            .Where(m => !NamesStoredData.Contains(m.Groups[1].Value))
            .Where(m => m.Groups[2].Value.Contains("SimplArchive", StringComparison.Ordinal)
                || m.Groups[2].Value.Contains(Strings.DefaultProductName, StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(literal.Count == 0,
            $"These texts spell the product's name out instead of saying {Strings.ProductToken}, so a rename misses them:\n  "
            + string.Join("\n  ", literal));
    }

    // The shared client libraries the desktop and the phone show text from: a C# literal naming the product escapes the
    // resource check above (the key test's report said "SimplArchive hardware key test" after the rename, Mobile #187).
    // Comments, dotted identifiers ("SimplArchive.ApiClient") and the X-SimplArchive- header names are not texts.
    [Theory]
    [InlineData("SimplArchive.ApiClient")]
    [InlineData("SimplArchive.Presentation")]
    [InlineData("SimplArchive.SealedArchive")]
    [InlineData("SimplArchive.TextLayout")]
    public void No_shared_client_literal_spells_the_product_name_out(string project)
    {
        var root = RepoPaths.Root();
        var spelled = Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && Literal.IsMatch(l.Text))
            .Where(l => !l.Text.Contains("LogWarning", StringComparison.Ordinal) && !l.Text.Contains("LogInformation", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.True(spelled.Count == 0,
            $"These literals spell the product's name out; use Strings.ProductName:\n  {string.Join("\n  ", spelled)}");
    }

    private static readonly Regex Literal = new(@"""[^""]*\b(SimplArchive|Alcifra)\b(?!\.[A-Za-z])(?!-)[^""]*""", RegexOptions.Compiled);

    // Phase 1 of the rename (core #1117): the server, the web, the desktop and the tools name the product through
    // Strings.ProductName (Branding:ProductName) or the build's ProductBuild.Name. What still spells the old name is
    // listed here, each with the step that moves it; a new literal fails.
    private static readonly Dictionary<string, string> NotYetRenamed = new()
    {
        // Names already written into installations and users' machines, kept whatever the product is called: the
        // data-protection application, the desktop's app-data, keychain and temp names, the earlier product name the
        // heals recognise (#1117 phase 5). One file, which the code sweep excludes.
        ["src/SimplArchive.Localization/PersistedNames.cs"] = "names with data behind them; renaming one orphans what was stored under it",
        // Phase 5, identifiers that move with the code sweep.
        ["src/SimplArchive.DesktopClient/Views/WindowShots.cs"] = "phase 5: a screenshot fixture naming the desktop package, renamed with the updater and the release repository",
        ["src/SimplArchive.Api/Serialization/UtcModelNormalizer.cs"] = "an identifier: the code's own namespace prefix",
        ["src/SimplArchive.DesktopClient/Services/ClientUpdate.cs"] = "phase 5: the release repository's name",
    };

    [Theory]
    [InlineData("SimplArchive.Api")]
    [InlineData("SimplArchive.Client")]
    [InlineData("SimplArchive.DesktopClient")]
    [InlineData("SimplArchive.Infrastructure")]
    [InlineData("SimplArchive.Domain")]
    [InlineData("SimplArchive.Localization")]
    [InlineData("SimplArchive.Cli")]
    [InlineData("SimplArchive.BrowserHost")]
    public void No_server_or_client_text_spells_the_product_name_out(string project)
    {
        var root = RepoPaths.Root();
        string[] kinds = [".cs", ".razor", ".cshtml", ".axaml", ".html"];
        var spelled = Directory.EnumerateFiles(Path.Combine(root, "src", project), "*", SearchOption.AllDirectories)
            .Where(f => kinds.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)
                && !f.Contains("/wwwroot/download/", StringComparison.Ordinal) && !NotYetRenamed.ContainsKey(f))
            .Where(f => SpelledOut(File.ReadAllText(Path.Combine(root, f))))
            .ToList();

        Assert.True(spelled.Count == 0,
            "These files spell the product's name out; use Strings.ProductName (a text) or ProductBuild.Name (an "
            + $"identifier that follows the build), or list the file in NotYetRenamed with its step:\n  {string.Join("\n  ", spelled)}");
    }

    // A string literal or element text naming the product, outside comments, log lines, dotted identifiers
    // ("SimplArchive.Api") and X-SimplArchive- header names.
    private static bool SpelledOut(string text)
    {
        var code = Comments.Replace(text, string.Empty);
        return code.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(line => !LogCall.IsMatch(line))
            .Select(line => Identifier.Replace(line, string.Empty))
            .Any(line => Named.IsMatch(line));
    }

    private static readonly Regex Comments = new(@"<!--.*?-->|@\*.*?\*@|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LogCall = new(@"\bLog(Trace|Debug|Information|Warning|Error|Critical)\b|\bLog\.(Fatal|Information|Warning|Error)\b", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"SimplArchive[.\-][A-Za-z]|X-SimplArchive", RegexOptions.Compiled);
    private static readonly Regex Named = new(@"""[^""\n]*\bSimplArchive\b[^""\n]*""|>[^<\n]*\bSimplArchive\b[^<\n]*<", RegexOptions.Compiled);
}
