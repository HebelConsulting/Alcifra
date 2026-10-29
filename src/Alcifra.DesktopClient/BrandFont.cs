using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Alcifra.Theming;

namespace Alcifra.DesktopClient;

/// <summary>
/// The brand's typeface on the desktop (ADR 0991): the static faces in <c>brand/assets/fonts</c>, embedded as
/// Avalonia resources and registered as one font collection, so a white-label build changes <c>brand/</c> and the
/// desktop follows.
/// </summary>
/// <remarks>
/// It replaces the framework's Inter package, which pinned the typeface in this project rather than in the brand.
/// Every <see cref="AppBuilder"/> in the app (the real one and each headless hook) goes through
/// <see cref="WithBrandFont"/>, so a screenshot is set in what the user sees.
/// </remarks>
public static class BrandFont
{
    private const string CollectionKey = "fonts:Brand";

    /// <summary>
    /// The brand's UI family inside the embedded collection, then the platform's default. The fallback is not
    /// decoration: a headless hook that never calls <see cref="WithBrandFont"/> has no such collection, and a family
    /// with nothing to fall back to throws while measuring text (it did, in two hooks), where Fluent's own value had
    /// always fallen back.
    /// </summary>
    public static FontFamily Ui { get; } = new($"{CollectionKey}#{Brand.Shipped.Fonts.Ui}, $Default");

    public static AppBuilder WithBrandFont(this AppBuilder builder) =>
        builder
            .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
                new Uri(CollectionKey, UriKind.Absolute),
                new Uri("avares://Alcifra.DesktopClient/Assets/Fonts", UriKind.Absolute))))
            .With(new FontManagerOptions { DefaultFamilyName = $"{CollectionKey}#{Brand.Shipped.Fonts.Ui}" });
}
