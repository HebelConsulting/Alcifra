using System.Text.Json;

namespace Alcifra.EndToEndTests;

// One runtime brand pack written to a temp directory and mounted exactly as the kiosk mounts its packs
// (ADR 1103, #2408) — the whole leg runs with it configured, and NO tenant carries it until a test assigns it
// through the platform-administrator endpoint. That is what makes it safe to mount for everyone: an unassigned
// tenant resolves to the installation's own brand, which is the state every other test already asserts.
internal static class BrandPackFixture
{
    public const string PackId = "kiosk-demo";
    public const string ProductName = "Kiosk Demo";

    /// <summary>The default palette's light accent — the colour a landing-page test can look for.</summary>
    public const string DefaultAccent = "#1F5FAD";

    /// <summary>A second palette's light accent, chosen in self-service and never shown to a stranger.</summary>
    public const string GreenAccent = "#2F6B3A";

    private static readonly Lazy<string> Root = new(Write);

    /// <summary>The directory <c>Brand:PacksDirectory</c> points at.</summary>
    public static string Directory() => Root.Value;

    private static string Write()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alcifra-e2e-brand-packs-{Guid.NewGuid():N}");
        var pack = Path.Combine(root, PackId);
        System.IO.Directory.CreateDirectory(pack);

        File.WriteAllText(Path.Combine(pack, "wordmark-light.svg"), Svg(DefaultAccent));
        File.WriteAllText(Path.Combine(pack, "wordmark-dark.svg"), Svg("#4C8FD9"));
        File.WriteAllText(Path.Combine(pack, "favicon.svg"), Svg(DefaultAccent));

        File.WriteAllText(Path.Combine(pack, "pack.json"), JsonSerializer.Serialize(new
        {
            productName = ProductName,
            colors = Palette(DefaultAccent, "#4C8FD9", "#06192F"),
            colorVariants = new Dictionary<string, object>
            {
                // Two variants, so the self-service chooser has something to offer when the tenant allows it.
                ["blue"] = Variant("Blue", "Blau", DefaultAccent, "#4C8FD9", "#06192F"),
                ["green"] = Variant("Green", "Grün", GreenAccent, "#7FD18F", "#06230D"),
            },
            assets = new
            {
                wordmarkLight = "wordmark-light.svg",
                wordmarkDark = "wordmark-dark.svg",
                favicon = "favicon.svg",
            },
        }));

        return root;
    }

    private static string Svg(string colour) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='40' height='12'><rect width='40' height='12' fill='{colour}'/></svg>";

    // Every colour of a palette is stated (ADR 1103 refuses a partial one). The neutrals carry legibility, so the
    // same legible set is used everywhere; a variant changes the accent pair that identifies it.
    private static object Palette(string primaryLight, string primaryDark, string onPrimaryDark) => new
    {
        light = Light(primaryLight),
        dark = Dark(primaryDark, onPrimaryDark),
    };

    private static object Variant(string en, string de, string primaryLight, string primaryDark, string onPrimaryDark) => new
    {
        name = new { en, de },
        light = Light(primaryLight),
        dark = Dark(primaryDark, onPrimaryDark),
    };

    private static object Light(string primary) => new
    {
        accent = new
        {
            primary,
            hover = "#0D6A62",
            onPrimary = "#FFFFFF",
            text = "#0B5D56",
            tint = "#E6F2F1",
            selection = "#CCE7E4",
        },
        semantic = new { danger = "#B91C1C", success = "#15803D", warning = "#A16207" },
        neutral = new
        {
            canvas = "#FAFAFC",
            surface = "#FFFFFF",
            sunken = "#F1F2F5",
            hairline = "#E6E7EC",
            text = "#14161C",
            textSecondary = "#5A5F6E",
            textFaint = "#898F9E",
            folder = "#D9A441",
        },
    };

    private static object Dark(string primary, string onPrimary) => new
    {
        accent = new
        {
            primary,
            hover = "#5BD9CE",
            onPrimary,
            text = "#9CC7F0",
            tint = "#123B36",
            selection = "#1B4B45",
        },
        semantic = new { danger = "#F87171", success = "#4ADE80", warning = "#FBBF24" },
        neutral = new
        {
            canvas = "#0F1115",
            surface = "#171A20",
            sunken = "#20242C",
            hairline = "#2C313B",
            text = "#E8EAEF",
            textSecondary = "#A6ACB9",
            textFaint = "#767C89",
            folder = "#E0B45C",
        },
    };
}
