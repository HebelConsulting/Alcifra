using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Svg;
using SimplArchive.Localization;
using SimplArchive.Theming;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The brand's wordmark on the desktop (ADR 0992): its two SVGs, embedded from <c>brand/assets</c>, as the theme
/// resource <c>WbWordmark</c>, so the title bar and the logon window switch between them with light and dark.
/// </summary>
public static class BrandWordmarkImages
{
    private const string ResourceKey = "WbWordmark";

    /// <summary>Whether the logon window shows the wordmark: while the product carries the brand's name.</summary>
    public static bool Shown => Brand.ShowsWordmark(Strings.ProductName);

    /// <summary>The opposite of <see cref="Shown"/>, for the name-as-text fallback beside it.</summary>
    public static bool Hidden => !Shown;

    /// <summary>Adds the light and dark wordmark to the shipped design's theme dictionaries.</summary>
    public static void AddTo(ResourceDictionary shipped)
    {
        Add(shipped, ThemeVariant.Light, Brand.Shipped.Wordmark.Light);
        Add(shipped, ThemeVariant.Dark, Brand.Shipped.Wordmark.Dark);
    }

    private static void Add(ResourceDictionary shipped, ThemeVariant variant, string file)
    {
        var source = SvgSource.Load($"avares://SimplArchive.DesktopClient/Assets/Brand/{file}", baseUri: null);
        ((ResourceDictionary)shipped.ThemeDictionaries[variant])[ResourceKey] = new SvgImage { Source = source };
    }
}
