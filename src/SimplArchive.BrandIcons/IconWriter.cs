using System.Buffers.Binary;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg;
using SimplArchive.Theming;

namespace SimplArchive.BrandIcons;

/// <summary>
/// Writes every launcher icon the three desktop platforms want, from the one piece of art (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// The art itself is <see cref="IconArt"/>, which now takes its colours from the design tokens — so when the
/// brand changes, the icon changes with it instead of contradicting it. That is the whole reason this exists:
/// the icon is the half of a rebrand nobody remembers, and an application whose window is one colour and whose
/// Dock tile is another looks unfinished in a way users notice before they can name.
/// </para>
/// <para>
/// <b>No external tools.</b> The previous sizes were cut once with ImageMagick, and
/// <c>package-windows-linux.sh</c> records what that cost: adding ImageMagick to the packaging preflight broke
/// the Docker image build, and with it the Trivy scan. So the containers are written here — both are simple
/// enough that a dependency would buy nothing. ICO is a directory of PNGs; ICNS is a magic word, a length, and
/// a series of typed PNG chunks.
/// </para>
/// <para>
/// Nothing it writes is committed (ADR 0993): the build runs it before the desktop app and the API compile, into
/// <c>artifacts/brand-icons</c>, so a white-label build changes <c>brand/</c> and every icon follows.
/// </para>
/// </remarks>
public static class IconWriter
{
    private const int Source = 1024;

    // Apple's macOS app-icon grid (#787): on a 1024 canvas the rounded tile is 824 square and centred, with the
    // remaining ~100px of padding carrying the drop shadow. Every other platform is full-bleed, so this applies
    // to the .icns ALONE — see RenderMacOs.
    private const double MacTile = 824;

    // IconArt draws a 224px corner on a 1024 tile; the shadow must follow the same squircle, not a plain box.
    private const double TileRadiusRatio = 224.0 / 1024.0;

    // Windows: the sizes Explorer picks between, from the tray to the 256px "extra large" view.
    private static readonly int[] IcoSizes = [16, 32, 48, 64, 96, 128, 256];

    // A browser tab, a bookmark bar and a pinned-site tile. Smaller than the launcher's set: nothing asks a
    // favicon for 256px, and every size is bytes on every page load.
    private static readonly int[] FaviconSizes = [16, 32, 48, 64];

    // Linux hicolor: what the .desktop entry's installer copies into share/icons/hicolor/<n>x<n>/apps.
    private static readonly int[] HicolorSizes = [48, 64, 128, 256];

    // macOS: (chunk type, pixel size). The duplicated sizes are not a mistake — ic11..ic14 are the Retina
    // variants of 16/32/128/256pt, and a .icns without them is upscaled by the system on a Retina display.
    // ICNS chunk types, each a PNG at the size given.
    //
    // `icp4` (16) and `icp5` (32) are DELIBERATELY ABSENT (#787), and this is not a size the icon lacks — it is
    // one it used to carry corrupted. Those two type codes predate PNG-in-ICNS and overlap the legacy raw-pixel
    // formats, so readers interpret their payload as raw ARGB: our perfectly valid PNG bytes were drawn as
    // PIXEL data, which is why the 16px and 32px representations rendered as random colour noise with alpha 1.0
    // at every pixel while every larger size was fine. Verified by dumping both, and by the control that proves
    // the reader innocent — Apple's own Notes 16px dumps as clean colours with graded alpha.
    //
    // Apple's own icons do not use them either: Notes.icns carries ic13/ic11/ic04/ic07 and no icp*. macOS is
    // content to downscale, so 16px now comes from `ic11` (32px) — an exact 2:1 reduction, which is the
    // cleanest scale there is. Carrying every size was never the requirement; carrying correct ones is.
    private static readonly (string Type, int Size)[] IcnsChunks =
    [
        ("ic11", 32), ("ic12", 64),
        ("ic07", 128), ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
    ];

    /// <summary>
    /// The two command-line hooks: <c>--gen-icons [dir]</c> writes every artefact and names each file on
    /// stdout; <c>--gen-icon &lt;out.png&gt;</c> writes the art alone, which is what answers "what does it look
    /// like" without producing eight files.
    /// </summary>
    /// <remarks>
    /// The dispatch and the argument walk live here rather than in Program.cs — which is on the 1000-line
    /// standing-debt list, and which has no business knowing where icons go.
    /// </remarks>
    /// <summary>
    /// Writes every icon artefact into <paramref name="outputDirectory"/>. The desktop's files sit at its top; the
    /// web's under <c>web/</c>, laid out as they sit in the published <c>wwwroot</c>. Returns what it wrote.
    /// </summary>
    /// <param name="brandAssets">The brand's <c>brand/assets</c>, where an SVG icon the brand names lives.</param>
    public static IReadOnlyList<string> Write(string outputDirectory, string brandAssets)
    {
        Directory.CreateDirectory(outputDirectory);
        var art = Art(brandAssets);
        using var master = Render(art);
        var written = new List<string>();

        var master1024 = Path.Combine(outputDirectory, "icon-1024.png");
        Save(master, Source, master1024);
        written.Add(master1024);

        // The in-app window icon (Avalonia loads it for every window's title bar and the taskbar entry).
        var window = Path.Combine(outputDirectory, "icon.png");
        Save(master, 256, window);
        written.Add(window);

        var ico = Path.Combine(outputDirectory, "app.ico");
        File.WriteAllBytes(ico, BuildIco(master));
        written.Add(ico);

        // The macOS icon is composed on Apple's grid, from its OWN master (#787) — everything else above and
        // below stays full-bleed, which is the convention on every other platform.
        var icns = Path.Combine(outputDirectory, "app.icns");
        using (var macMaster = RenderMacOs(art))
        {
            File.WriteAllBytes(icns, BuildIcns(macMaster));
        }

        written.Add(icns);

        var linux = Path.Combine(outputDirectory, "linux");
        Directory.CreateDirectory(linux);
        foreach (var size in HicolorSizes)
        {
            var path = Path.Combine(linux, $"{size}.png");
            Save(master, size, path);
            written.Add(path);
        }

        // The browsers' icon, from the same art (ADR 0578), served by the API to the app, the sign-in page and the
        // external-link landing page alike. A .ico as well as the .png: Safari and older Explorer ask for
        // /favicon.ico by name whatever the page declares, and a 404 there is the one console error a visitor
        // sees on a page that is otherwise fine.
        var web = Path.Combine(outputDirectory, "web");
        Directory.CreateDirectory(web);
        var png = Path.Combine(web, "favicon.png");
        Save(master, 256, png);
        written.Add(png);

        var favicon = Path.Combine(web, "favicon.ico");
        File.WriteAllBytes(favicon, BuildIco(master, FaviconSizes));
        written.Add(favicon);

        // Recoloured per style only while the icon is the drawn one: a brand's own SVG has no accent to swap.
        if (art is null)
        {
            written.AddRange(WriteStyleFavicons(web));
        }

        return written;
    }

    // The brand's own icon (ADR 0993) when brand.json names one, else null: the drawn cabinet in its colours.
    private static SvgImage? Art(string brandAssets) =>
        Brand.Shipped.Icon is { } file
            ? new SvgImage { Source = SvgSource.Load(Path.Combine(brandAssets, file), baseUri: null) }
            : null;

    // The art as a 1024 visual: the brand's SVG scaled into the square, or the drawn tile.
    private static Control Visual(SvgImage? art, AccentTokens? accent = null) =>
        art is null
            ? IconArt.BuildTile(accent)
            : new Image { Source = art, Width = Source, Height = Source, Stretch = Stretch.Uniform };

    /// <summary>
    /// One favicon per bundled style, served from the download area (ADR 0578).
    /// </summary>
    /// <remarks>
    /// The tab icon cannot follow a runtime theme — it is a file, and the Alpine image has no rasteriser — so
    /// an installation that sets <c>custom/theme.json</c> to indigo would otherwise keep a teal tab. These are
    /// the matching set: pick the style, copy the icon of the same name into <c>custom/</c>, done. Listed by
    /// the themed directory browser at <c>/download/themes/</c>, so nobody has to be told where they are.
    /// </remarks>
    private static IEnumerable<string> WriteStyleFavicons(string web)
    {
        var directory = Path.Combine(web, "download", "themes");
        Directory.CreateDirectory(directory);

        foreach (var style in ThemeCatalog.Available().Where(s => s.Bundled))
        {
            var load = ThemeCatalog.Load(style.Id);
            using var master = Render(art: null, load.Tokens.Light.Accent);

            var path = Path.Combine(directory, $"{style.Id}.ico");
            File.WriteAllBytes(path, BuildIco(master, FaviconSizes));
            yield return path;
        }
    }

    // Rendered once at 1024 and scaled down, rather than re-rendered per size. The art is rounded rectangles,
    // which a high-quality downscale handles cleanly; rendering the same vector at 16px would instead put the
    // tile's 224px corner radius through a layout pass that was never designed for it.
    private static Bitmap Render(SvgImage? art, AccentTokens? accent = null)
    {
        var visual = Visual(art, accent);
        visual.Measure(new Size(Source, Source));
        visual.Arrange(new Rect(0, 0, Source, Source));

        // Starts fully transparent and only paints the visual, so the tile's rounded corners stay transparent —
        // a window capture would bake an opaque background behind them.
        using var target = new RenderTargetBitmap(new PixelSize(Source, Source), new Vector(96, 96));
        target.Render(visual);

        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    /// <summary>The macOS master: the same art, composed onto Apple's icon grid.</summary>
    /// <remarks>
    /// <para>
    /// macOS is the only platform that expects an app icon to be INSET rather than full-bleed. Measured against
    /// real icons on a Mac, the tile occupies ~80% of the canvas — Apple's 824/1024 — with the shadow hanging
    /// into the padding below (Notes and Reminders: 214/256 art, inset T24 B18; Affinity Designer: 850/1024,
    /// inset T97 B77). Ours filled the canvas edge to edge, so in /Applications it drew at roughly 1.5x the AREA
    /// of every neighbour, square to the bounds and flat where the others have depth. That is what "distorted"
    /// meant: not a wrong aspect ratio, a broken grid rhythm.
    /// </para>
    /// <para>
    /// <b>Why this is a second render rather than a change to <see cref="IconArt.BuildTile"/>.</b> One master
    /// feeds five artefacts — the .icns, the Windows .ico, the in-app window icon, cabinet-1024.png and the WEB
    /// client's favicons. Padding the shared art would shrink the favicon in every browser tab and the taskbar
    /// icon with it, to satisfy a convention only macOS has. The inset belongs here and nowhere else.
    /// </para>
    /// <para>
    /// A <see cref="Viewbox"/> rather than a smaller tile: <c>BuildTile</c> lays its drawers out in absolute
    /// pixels against a 1024 canvas, so shrinking the frame alone would leave them proportionally oversized.
    /// Scaling the finished art keeps every ratio — corner radius included — exactly as designed.
    /// </para>
    /// </remarks>
    private static Bitmap RenderMacOs(SvgImage? art, AccentTokens? accent = null)
    {
        var scaled = new Viewbox
        {
            Width = MacTile,
            Height = MacTile,
            Child = Visual(art, accent),
        };

        // The shadow is a Border BOX-SHADOW, not a DropShadowEffect. An Effect is applied by the COMPOSITION
        // renderer and never reaches `RenderTargetBitmap`, which draws immediately — measured twice, on the tile
        // and on the canvas: alpha went 1.000 at y=920 to 0.000 at y=930, a hard cutoff, where a real icon reads
        // 1.000 → 0.192 → 0.078 → 0 across the same span. Nothing failed and nothing warned; the shadow simply
        // was not there. BoxShadow draws in the immediate path, so it survives the render-to-bitmap.
        //
        // Offset DOWN, which is why real icons measure more padding above than below (Affinity: T97 B77), and
        // sized to stay inside the 100px margin (16 offset + 32 blur) so nothing clips at the canvas edge. The
        // corner radius is the tile's own ratio, so the shadow follows the squircle rather than a plain box.
        var shadowed = new Border
        {
            Width = MacTile,
            Height = MacTile,
            CornerRadius = new CornerRadius(MacTile * TileRadiusRatio),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // The shadow follows the drawn tile's squircle; a brand's own SVG brings whatever depth it wants.
            BoxShadow = art is null ? BoxShadows.Parse("0 16 32 0 #47000000") : default,
            Child = scaled,
        };

        var canvas = new Panel { Width = Source, Height = Source, Children = { shadowed } };
        canvas.Measure(new Size(Source, Source));
        canvas.Arrange(new Rect(0, 0, Source, Source));

        using var target = new RenderTargetBitmap(new PixelSize(Source, Source), new Vector(96, 96));
        target.Render(canvas);

        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    private static void Save(Bitmap master, int size, string path)
    {
        if (size == Source)
        {
            master.Save(path);
            return;
        }

        using var scaled = master.CreateScaledBitmap(new PixelSize(size, size), BitmapInterpolationMode.HighQuality);
        scaled.Save(path);
    }

    private static byte[] Png(Bitmap master, int size)
    {
        using var stream = new MemoryStream();
        if (size == Source)
        {
            master.Save(stream);
        }
        else
        {
            using var scaled = master.CreateScaledBitmap(new PixelSize(size, size), BitmapInterpolationMode.HighQuality);
            scaled.Save(stream);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// ICONDIR, one 16-byte ICONDIRENTRY per size, then the PNG payloads.
    /// </summary>
    /// <remarks>
    /// PNG payloads for every entry rather than BMP for the small ones. Windows has accepted PNG-in-ICO since
    /// Vista, and the BMP path needs its own AND mask — a second, differently-encoded copy of the transparency
    /// that is easy to get subtly wrong and impossible to notice until an icon has a black fringe at 16px.
    /// </remarks>
    private static byte[] BuildIco(Bitmap master, int[]? sizes = null)
    {
        var chosen = sizes ?? IcoSizes;
        var images = chosen.Select(size => Png(master, size)).ToList();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write((ushort)0);                  // reserved
        writer.Write((ushort)1);                  // 1 = icon
        writer.Write((ushort)images.Count);

        var offset = 6 + (16 * images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            // 256 is written as 0: the field is one byte, and 256 does not fit in it.
            writer.Write((byte)(chosen[i] == 256 ? 0 : chosen[i]));
            writer.Write((byte)(chosen[i] == 256 ? 0 : chosen[i]));
            writer.Write((byte)0);                // palette size — 0 for truecolour
            writer.Write((byte)0);                // reserved
            writer.Write((ushort)1);              // colour planes
            writer.Write((ushort)32);             // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }

        writer.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// The 'icns' magic, the total file length, then one typed chunk per variant.
    /// </summary>
    /// <remarks>
    /// Every length in this format is <b>big-endian</b> and <b>includes its own 8-byte header</b> — the two
    /// details that make a hand-written .icns silently unreadable rather than loudly broken.
    /// </remarks>
    private static byte[] BuildIcns(Bitmap master)
    {
        var chunks = IcnsChunks.Select(c => (c.Type, Data: Png(master, c.Size))).ToList();
        var total = 8 + chunks.Sum(c => 8 + c.Data.Length);

        using var output = new MemoryStream(total);
        output.Write(Encoding.ASCII.GetBytes("icns"));
        output.Write(BigEndian(total));

        foreach (var (type, data) in chunks)
        {
            output.Write(Encoding.ASCII.GetBytes(type));
            output.Write(BigEndian(8 + data.Length));
            output.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
