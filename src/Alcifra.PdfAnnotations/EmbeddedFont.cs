using PdfSharp.Fonts;

namespace Alcifra.PdfAnnotations;

/// <summary>
/// The text faces the writer embeds — Noto Sans Regular as the primary, SIL OFL, carried inside the package so a
/// flattened caption or a signature's typed name renders identically on the Api, the desktop and a phone, with no
/// system font consulted (PDFsharp asks a resolver; without one it refuses to draw text at all) — and Noto Sans
/// Symbols 2, also SIL OFL, for the characters the primary lacks (#2451: ✓ U+2713 and its kind; PDFsharp 6.2 has no
/// per-glyph fallback, so the writer asks <see cref="Covers"/> and picks the face per run itself).
/// </summary>
public sealed class EmbeddedFont : IFontResolver
{
    /// <summary>The family name every drawn string asks for.</summary>
    public const string Family = "Noto Sans";

    /// <summary>The fallback family for characters outside the primary's repertoire (#2451).</summary>
    public const string SymbolsFamily = "Noto Sans Symbols 2";

    private const string PrimaryFile = "NotoSans-Regular.ttf";
    private const string SymbolsFile = "NotoSansSymbols2-Regular.ttf";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, byte[]> Loaded = [];
    private static readonly Dictionary<string, FontCoverage> Coverage = [];
    private static bool _registered;

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

    /// <summary>Whether the PRIMARY face has a real glyph for <paramref name="c"/>.</summary>
    internal static bool Covers(char c) => CoverageOf(Family).Covers(c);

    /// <summary>Whether the SYMBOLS face has a real glyph for <paramref name="c"/>.</summary>
    internal static bool SymbolsCover(char c) => CoverageOf(SymbolsFamily).Covers(c);

    internal static FontCoverage CoverageOf(string family) => LoadCoverage(family);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Two faces are embedded; a bold or italic request takes the face it named rather than failing the export.
        if (string.Equals(familyName, SymbolsFamily, StringComparison.OrdinalIgnoreCase))
        {
            return new FontResolverInfo(SymbolsFamily);
        }

        return string.Equals(familyName, Family, StringComparison.OrdinalIgnoreCase) ? new FontResolverInfo(Family) : null;
    }

    public byte[]? GetFont(string faceName) => faceName switch
    {
        Family => Load(PrimaryFile, Family),
        SymbolsFamily => Load(SymbolsFile, SymbolsFamily),
        _ => null,
    };

    private static byte[] Load(string file, string face)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(face, out var cached))
            {
                return cached;
            }

            using var stream = typeof(EmbeddedFont).Assembly.GetManifestResourceStream($"Alcifra.PdfAnnotations.Fonts.{file}")
                ?? throw new PdfEmbeddingException($"The embedded font {file} is missing from the package; the flattened export cannot draw text.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Loaded[face] = buffer.ToArray();
        }
    }

    private static FontCoverage LoadCoverage(string family)
    {
        lock (Gate)
        {
            if (Coverage.TryGetValue(family, out var cached))
            {
                return cached;
            }

            var coverage = FontCoverage.Read(Load(family == SymbolsFamily ? SymbolsFile : PrimaryFile, family));
            return Coverage[family] = coverage;
        }
    }
}
