using Alcifra.Theming;
using Microsoft.Extensions.FileProviders;

namespace Alcifra.Api.Branding;

/// <summary>
/// The palette this installation wears (#2319): the brand's own colours, or the variant an operator selected with
/// <c>Branding:Variant</c> (e.g. Alcifra Bund's blue for blue-light organisations, green for the military). One palette
/// for the whole installation, the sign-in page included, because that page is shown before any tenant is known.
/// </summary>
/// <remarks>
/// Resolved once, while the app starts: an id the brand does not carry stops the start with the ids it does carry,
/// rather than quietly showing the default. An operator's <c>custom/theme.json</c> is laid over this palette, not over
/// the brand's default.
/// </remarks>
public sealed class InstallationPalette
{
    public const string VariantKey = "Branding:Variant";

    internal InstallationPalette(string? variant, ThemeTokens tokens) => (Variant, Tokens) = (variant, tokens);

    /// <summary>The selected variant's id, or null for the brand's own colours.</summary>
    public string? Variant { get; }

    public ThemeTokens Tokens { get; }

    /// <summary>Where the brand's own manual lives under wwwroot (ADR 0997): the path every manual link names.</summary>
    public static readonly string DefaultManualPath = $"download/manual/{Brand.Shipped.ManualFileName}";

    /// <summary>
    /// The manual this installation serves and files: the selected variant's, its figures in that palette, when the
    /// build carries it, else the brand's own. A build without the variant's manual still serves one.
    /// </summary>
    public string ManualPath(IFileProvider webRoot)
    {
        if (Variant is null)
        {
            return DefaultManualPath;
        }

        var own = $"download/manual/{Brand.Shipped.FileStem}-Manual-{Variant}.pdf";
        return webRoot.GetFileInfo(own).Exists ? own : DefaultManualPath;
    }

    /// <exception cref="InvalidOperationException">The setting names a palette the brand does not carry.</exception>
    public static InstallationPalette From(IConfiguration configuration)
    {
        var variant = configuration[VariantKey] is { Length: > 0 } configured && configured.Trim() is { Length: > 0 } id ? id : null;
        try
        {
            return new InstallationPalette(variant, ThemeTokensReader.Of(variant));
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"{VariantKey}: {e.Message}", e);
        }
    }
}
