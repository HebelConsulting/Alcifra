using PdfSharp.Fonts;

namespace Alcifra.PdfAnnotations;

/// <summary>
/// The one text face the writer embeds — Noto Sans Regular, SIL OFL, carried inside the package so a flattened caption
/// or a signature's typed name renders identically on the Api, the desktop and a phone, with no system font consulted
/// (PDFsharp asks a resolver; without one it refuses to draw text at all).
/// </summary>
public sealed class EmbeddedFont : IFontResolver
{
    /// <summary>The family name every drawn string asks for.</summary>
    public const string Family = "Noto Sans";

    private static readonly Lock Gate = new();
    private static bool _registered;
    private static byte[]? _bytes;

    /// <summary>Makes the resolver PDFsharp's own, once per process (it is a global setting).</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            // Another host may already have a resolver; theirs stays — a writer must not fight the process.
            GlobalFontSettings.FontResolver ??= new EmbeddedFont();
            _registered = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // One face is embedded; a bold or italic request takes it rather than failing the export.
        return string.Equals(familyName, Family, StringComparison.OrdinalIgnoreCase) ? new FontResolverInfo(Family) : null;
    }

    public byte[]? GetFont(string faceName)
    {
        if (!string.Equals(faceName, Family, StringComparison.Ordinal))
        {
            return null;
        }

        if (_bytes is not null)
        {
            return _bytes;
        }

        using var stream = typeof(EmbeddedFont).Assembly.GetManifestResourceStream("Alcifra.PdfAnnotations.Fonts.NotoSans-Regular.ttf")
            ?? throw new PdfEmbeddingException("The embedded font is missing from the package; the flattened export cannot draw text.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        _bytes = buffer.ToArray();
        return _bytes;
    }
}
