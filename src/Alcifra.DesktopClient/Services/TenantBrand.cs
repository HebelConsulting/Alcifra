using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.Localization;
using Alcifra.Theming;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Svg;
using Avalonia.Threading;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The brand the signed-in caller wears (ADR 1103, #2408): the tenant's runtime pack — product name, palette, wordmark
/// and icon — applied after sign-in and forgotten on sign-out. The logon window never asks: it is anonymous, and the
/// installation's brand is what every anonymous surface paints.
/// </summary>
/// <remarks>
/// <para>
/// The resource is fetched through the root's <c>brand</c> rel (ADR 0543), which the server advertises only to a
/// caller with a tenant. The ASSET FILES are cached in app-data by the pack's version — a content hash — so a restart
/// re-applies without downloading, and a pack replaced on the server re-caches by itself. What is never cached is the
/// RESOURCE: the effective palette differs per tenant and per user (the self-service override), so the small
/// authenticated read stays the authority on every sign-in.
/// </para>
/// <para>
/// Best effort throughout: a failure leaves the installation's brand on screen, which is the same state a tenant with
/// no pack has. A palette is applied through <see cref="ThemeApplier"/>, so it layers over whatever style the
/// profile had and reverts when the session ends.
/// </para>
/// </remarks>
public static class TenantBrand
{
    private const string WordmarkKey = "WbWordmark";
    private const string OverrideName = "Alcifra.TenantBrand";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ResourceDictionary? _wordmark;
    private static WindowIcon? _shippedIcon;

    /// <summary>Whether a pack is in force (the tenant carries a runtime brand).</summary>
    public static bool HasPack { get; private set; }

    /// <summary>Whether the pack brought a wordmark of its own — the title bar shows it in place of the name.</summary>
    public static bool ShowsWordmark { get; private set; }

    /// <summary>
    /// Fetch and apply the caller's brand. Answers whether a pack was applied; <c>false</c> means the tenant has no
    /// pack (or none could be read), and the installation's own brand is what should stay on screen.
    /// </summary>
    public static async Task<bool> ApplyAsync(AlcifraApiClient api, CancellationToken cancellationToken = default)
    {
        try
        {
            string href;
            try
            {
                href = await api.Core.RootHrefAsync("brand", cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The root advertises `brand` only once a tenant is known (ADR 1103) — its absence is the honest
                // "not available here", not an error.
                return false;
            }

            using var response = await api.Core.Http.GetAsync(href, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var brand = await response.Content.ReadFromJsonAsync<BrandResource>(Json, cancellationToken);
            if (brand is null || brand.PackId is null)
            {
                return false;
            }

            var tokens = Tokens(brand);
            var wordmarkLight = await AssetFileAsync(api, brand, BrandPackAssets.WordmarkLight, cancellationToken);
            var wordmarkDark = await AssetFileAsync(api, brand, BrandPackAssets.WordmarkDark, cancellationToken);
            var favicon = await AssetFileAsync(api, brand, BrandPackAssets.Favicon, cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HasPack = true;
                Strings.ProductName = brand.ProductName;

                if (tokens is not null)
                {
                    ThemeApplier.Apply(new ThemeTokensReader.ThemeLoad(tokens, Applied: true, []));
                }

                // COSMETIC, and each stands alone: a wordmark or icon that cannot load costs the mark, never the
                // brand. Before this, one raster wordmark threw inside this block and the whole apply failed — the
                // caller then fell back to the INSTALLATION's name, which is how a signed-in showcase tenant read
                // "Alcifra" in the title bar (2026-11-22, v0.62.6, the Pfeiffer pack's PNG).
                try { ApplyWordmark(wordmarkLight, wordmarkDark); }
                catch (Exception e) { DesktopLog.Warn(e, "The brand wordmark could not be applied"); }

                try { ApplyIcon(favicon); }
                catch (Exception e) { DesktopLog.Warn(e, "The brand icon could not be applied"); }
            });

            return true;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            DesktopLog.Warn(e, "The tenant brand could not be applied; the installation's brand stays");
            return false;
        }
    }

    /// <summary>Signed out: the installation's brand returns, with everything the pack brought.</summary>
    public static void Forget() => Dispatcher.UIThread.Post(() =>
    {
        HasPack = false;
        RemoveWordmark();

        // The palette the profile had before is re-applied by the logon window; carrying the pack's colours there
        // would paint a tenant's brand on a surface that must never show one.
        ThemeApplier.Apply(new ThemeTokensReader.ThemeLoad(ThemeTokensReader.Shipped, Applied: false, []));

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Icon = ShippedIcon();
        }
    });

    private static ThemeTokens? Tokens(BrandResource brand)
    {
        try
        {
            return Groups(brand.Theme, "light") is { } light && Groups(brand.Theme, "dark") is { } dark
                ? ThemeTokensReader.FromGroups(brand.ProductName, light, dark)
                : null;
        }
        catch (BrandPaletteInvalidException e)
        {
            // The server refused the pack at load; this is the belt to that brace, and never a page that fails.
            DesktopLog.Warn(e, "The brand palette could not be read; the installation's colours stay");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Groups(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>>? theme, string name) =>
        theme is not null
        && theme.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) is { Value: { } groups }
            ? groups.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value,
                StringComparer.OrdinalIgnoreCase)
            : null;

    // The asset's cached file, downloaded once per pack VERSION. Written through a temp file and moved into place,
    // so a crash mid-download never leaves a truncated wordmark the next start would happily paint.
    private static async Task<string?> AssetFileAsync(
        AlcifraApiClient api, BrandResource brand, string name, CancellationToken cancellationToken)
    {
        try
        {
            var href = brand.Links.FirstOrDefault(link =>
                link.Rel == "asset" && link.Href.EndsWith($"/{name}", StringComparison.OrdinalIgnoreCase))?.Href;
            if (href is null)
            {
                return null;
            }

            var directory = Path.Combine(CacheRoot, SafeVersion(brand.Version));
            var file = Path.Combine(directory, name);
            if (File.Exists(file))
            {
                return file;
            }

            using var response = await api.Core.Http.GetAsync(href, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Directory.CreateDirectory(directory);
            var temporary = $"{file}.tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, file, overwrite: true);
            PruneOtherVersions(Path.GetFileName(directory));
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            // A missing asset costs the mark, never the surface: the name shows as text, the icon stays ours.
            DesktopLog.Warn(e, "Brand asset {Asset} could not be cached", name);
            return null;
        }
    }

    private static void ApplyWordmark(string? light, string? dark)
    {
        RemoveWordmark();

        if ((light is null && dark is null) || Application.Current is not { } application)
        {
            return;
        }

        // The title bar's `{DynamicResource WbWordmark}` is per theme variant, so the pack's two files swap with
        // light and dark exactly as the installation's own (ADR 0992). A pack that declares one file wears it in both.
        var dictionary = new ResourceDictionary
        {
            [OverrideName] = true,
        };
        dictionary.ThemeDictionaries[ThemeVariant.Light] = Wordmark(light ?? dark);
        dictionary.ThemeDictionaries[ThemeVariant.Dark] = Wordmark(dark ?? light);
        application.Resources.MergedDictionaries.Add(dictionary);

        _wordmark = dictionary;
        ShowsWordmark = true;
    }

    private static ResourceDictionary Wordmark(string? path)
    {
        var variant = new ResourceDictionary();
        if (path is null)
        {
            return variant;
        }

        // A pack may ship its mark as SVG or as a raster (a customer's site often offers only PNG/WEBP). The loader
        // follows the FILE, not an assumption: SvgImage for .svg, Bitmap for everything else (2026-11-22).
        variant[WordmarkKey] = string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImage { Source = SvgSource.Load(path, baseUri: null) }
            : new Bitmap(path);
        return variant;
    }

    private static void RemoveWordmark()
    {
        ShowsWordmark = false;
        if (_wordmark is not null && Application.Current is { } application)
        {
            application.Resources.MergedDictionaries.Remove(_wordmark);
        }

        _wordmark = null;
    }

    private static void ApplyIcon(string? favicon)
    {
        // A browser's tab icon may be SVG; a window icon may not. Only a raster icon is taken — sniffed, never
        // trusted from the extension — and anything else leaves the installation's icon where it is.
        if (favicon is null || !IsRasterIcon(favicon))
        {
            return;
        }

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                window.Icon = new WindowIcon(favicon);
            }

            // macOS's Dock ignores Window.Icon, so the pack's icon goes to AppKit as well (the startup icon's own
            // route). Best effort: a Dock icon is cosmetic.
            if (OperatingSystem.IsMacOS())
            {
                MacDockIcon.TrySet(favicon);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DesktopLog.Warn(e, "The brand icon could not be applied");
        }
    }

    private static WindowIcon? ShippedIcon()
    {
        try
        {
            if (_shippedIcon is null)
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Alcifra.DesktopClient/Assets/icon.png"));
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                _shippedIcon = new WindowIcon(memory);
            }

            return _shippedIcon;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsRasterIcon(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[8];
            if (stream.Read(magic) < 8)
            {
                return false;
            }

            ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            ReadOnlySpan<byte> ico = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];
            return magic.SequenceEqual(png) || magic.SequenceEqual(ico);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // One directory per pack VERSION (a content hash), under the hidden app-data folder — never the user's visible
    // home folder, whose name follows the brand's file stem. Older versions are pruned when a new one arrives.
    private static string CacheRoot => Path.Combine(
        UserFolders.Get(Environment.SpecialFolder.LocalApplicationData),
        PersistedNames.DesktopAppData,
        "brand-cache");

    private static string SafeVersion(string? version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((version ?? "unversioned").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "unversioned" : cleaned;
    }

    // A pack replaced at deploy time gets a new version directory; the old one can never be used again. Pruning is
    // best effort — a cache that cannot be tidied must not stop the brand being painted.
    private static void PruneOtherVersions(string keep)
    {
        try
        {
            if (!Directory.Exists(CacheRoot))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
            {
                if (!string.Equals(Path.GetFileName(directory), keep, StringComparison.Ordinal))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Warn(e, "An older brand cache could not be pruned");
        }
    }

    private sealed record BrandResource
    {
        public string? PackId { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public string? PaletteId { get; set; }

        public bool CanChoosePalette { get; set; }

        public Dictionary<string, Dictionary<string, Dictionary<string, string>>>? Theme { get; set; }

        public string? Version { get; set; }

        public List<AssetLink> Links { get; set; } = [];
    }

    private sealed record AssetLink
    {
        public string Rel { get; set; } = string.Empty;

        public string Href { get; set; } = string.Empty;
    }
}
