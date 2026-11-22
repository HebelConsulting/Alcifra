using System.Net.Http.Json;
using Alcifra.Client.Hypermedia;
using Alcifra.Localization;
using Alcifra.Theming;
using Microsoft.JSInterop;
using MudBlazor;

namespace Alcifra.Client.Services;

/// <summary>
/// The brand the CALLER wears after signing in (ADR 1103, #2408): their tenant's runtime brand pack — product name,
/// palette tokens, wordmark and favicon — applied over the installation's own brand, which is what every anonymous
/// page and the first paint already wear.
/// </summary>
/// <remarks>
/// <para>
/// Fed by <c>GET /api/brand</c>, which is authenticated on purpose: no token, no tenant brand. This service is
/// therefore only asked to load once an identity exists, and it never asks on an anonymous surface. A tenant with
/// no pack answers with the installation brand and nothing is re-painted — an ordinary customer installation
/// behaves exactly as before.
/// </para>
/// <para>
/// The brand changes what a person READS, never what goes on the wire (ADR 0987): the product name here feeds
/// <see cref="Strings.ProductName"/>, which only ever appears as text, never in a route, media type or header.
/// </para>
/// <para>
/// Best effort by construction: the installation's brand is on screen before this runs, so a pack that cannot be
/// fetched or parsed costs the tenant's mark and colours, never the page. The server said its own side at load.
/// </para>
/// </remarks>
public sealed class RuntimeBrandState(HttpClient http, ApiRoot root, IJSRuntime js)
{
    /// <summary>What MudBlazor's components wear; the installation's theme until a pack brings its own.</summary>
    public MudTheme Theme { get; private set; } = AlcifraTheme.Shipped;

    /// <summary>The pack's colours as CSS custom properties, laid over <c>/api/theme.css</c>; null for no pack.</summary>
    public string? PaletteCss { get; private set; }

    /// <summary>The palettes the self-service chooser offers (the effective one carries <see cref="PaletteId"/>).</summary>
    public IReadOnlyList<PaletteOption> Palettes => _palettes;

    /// <summary>The palette in force, or null for the pack's own colours.</summary>
    public string? PaletteId => _paletteId;

    /// <summary>True only while the tenant allows a choice AND the brand carries more than one palette.</summary>
    public bool CanChoosePalette => _canChoosePalette;

    /// <summary>
    /// Whether the chrome shows the caller's brand MARK. A tenant pack's wordmark asset is the MARK, drawn beside the
    /// product name (ADR 1105); the installation's own wordmark spells the name, so it stands alone.
    /// </summary>
    public bool ShowsWordmark => _hasPack
        ? _packWordmarkLight is not null || _packWordmarkDark is not null
        : Brand.ShowsWordmark(Strings.ProductName);

    /// <summary>
    /// Whether a tenant pack is in force (ADR 1103). Its mark names the TENANT, not the product, so the product's
    /// name is written beside it (ADR 1105).
    /// </summary>
    public bool HasPack => _hasPack;

    /// <summary>The light-background wordmark: a data URL for a pack, the installation's served file otherwise.</summary>
    public string? WordmarkLight =>
        _hasPack ? _packWordmarkLight : ShowsWordmark ? WordmarkFile(Brand.Shipped.Wordmark.Light) : null;

    /// <summary>The dark-background wordmark.</summary>
    public string? WordmarkDark =>
        _hasPack ? _packWordmarkDark : ShowsWordmark ? WordmarkFile(Brand.Shipped.Wordmark.Dark) : null;

    /// <summary>Raised after a load or a palette change, so the chrome can re-render.</summary>
    public event Action? Changed;

    private List<LinkResponse>? _links;
    private List<PaletteOption> _palettes = [];
    private string? _paletteId;
    private bool _canChoosePalette;
    private bool _hasPack;
    private string? _packWordmarkLight;
    private string? _packWordmarkDark;

    /// <summary>One entry of the self-service chooser; <c>null</c> id means the tenant's default (clear the override).</summary>
    public sealed record PaletteOption(string? Id, string Name);

    /// <summary>
    /// Fetch and apply the caller's brand. Called once after authentication, before the workbench paints; safe to
    /// call again (a palette change re-reads the resolved result rather than deriving it here).
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            // Followed from the root's `brand` rel (ADR 0543), which the server advertises only once a tenant is
            // known — so an anonymous render cannot even reach an address that answers 401.
            if (await root.MemberHrefAsync("brand") is not { } href
                || await http.GetFromJsonAsync<BrandResource>(href) is not { } brand)
            {
                return;
            }

            _links = brand.Links;
            _paletteId = brand.PaletteId;
            _canChoosePalette = brand.CanChoosePalette;
            _palettes = brand.Palettes
                .Select(palette => new PaletteOption(palette.Id, NameOf(palette.Name, palette.Id)))
                .ToList();

            if (brand.PackId is null)
            {
                // The installation's own brand: already on screen, named by the root before anybody signed in.
                return;
            }

            _hasPack = true;

            if (brand.ProductName is { Length: > 0 } productName)
            {
                // Wherever the product names itself — every such text reads this one value, so it is set once.
                Strings.ProductName = productName;
            }

            if (Groups(brand.Theme, "light") is { } light && Groups(brand.Theme, "dark") is { } dark)
            {
                var tokens = ThemeTokensReader.FromGroups(brand.ProductName, light, dark);
                Theme = AlcifraTheme.From(tokens);
                PaletteCss = ThemeEmitter.PaletteCss(tokens);
            }

            // The asset endpoint is bearer-protected, so a raw <img src> would 401 — the bytes are fetched with the
            // authenticated client and shown as data URLs, the same way the profile photo is.
            _packWordmarkLight = await AssetAsync(brand.Links, BrandPackAssets.WordmarkLight);
            _packWordmarkDark = await AssetAsync(brand.Links, BrandPackAssets.WordmarkDark);

            if (await AssetAsync(brand.Links, BrandPackAssets.Favicon) is { } favicon)
            {
                await js.InvokeVoidAsync("simplArchiveBrand.setFavicon", favicon);
            }
        }
        catch (Exception)
        {
            // Keeps the installation's brand. A missing rel, a failed fetch, a pack whose palette was refused — none
            // of them may take the page down; the last one was already logged where the pack was loaded.
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The installation's own palette (ADR 0578), applied while no pack has won. A no-op once a pack is in force:
    /// the two arrive asynchronously, and the tenant's brand must never be clobbered by the later of the two.
    /// </summary>
    public void ApplyInstallation(ThemeTokens tokens)
    {
        if (_hasPack)
        {
            return;
        }

        Theme = AlcifraTheme.From(tokens);
    }

    /// <summary>
    /// The caller's own palette within the current brand (self-service, only while the tenant allows it and the
    /// brand offers a choice). The server resolves the result and this re-reads it, so the fallback order is never
    /// duplicated here.
    /// </summary>
    public async Task<bool> ChoosePaletteAsync(string? paletteId)
    {
        if (_links is null)
        {
            return false;
        }

        try
        {
            using var response = await Links.SendAsync(http, _links, "select-palette", new { paletteId });
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await LoadAsync();   // fires Changed
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The pack's theme arrives as the colour groups the pack file writes; both maps share the theme vocabulary.
    // The group names are matched case-insensitively because a pack is operator-written data and the server's loader
    // read it that way — a pack it accepted must not silently fall back here.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Groups(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>>? theme, string name) =>
        theme is not null
        && theme.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) is { Value: { } groups }
            ? groups.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value,
                StringComparer.OrdinalIgnoreCase)
            : null;

    private async Task<string?> AssetAsync(List<LinkResponse> links, string name)
    {
        try
        {
            var href = links.FirstOrDefault(link =>
                link.Rel == "asset" && link.Href.EndsWith($"/{name}", StringComparison.OrdinalIgnoreCase))?.Href;
            if (href is null)
            {
                return null;
            }

            using var response = await http.GetAsync(href.TrimStart('/'));
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var bytes = await response.Content.ReadAsByteArrayAsync();
            return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception)
        {
            // A missing wordmark falls back to the product name as text; a missing favicon keeps the tab's own.
            return null;
        }
    }

    private static string WordmarkFile(string file) => $"{BrandWordmark.UrlRoot}{file}";

    // The palette's own name, in the reader's language: the first of their language, then English, then whatever the
    // pack wrote, then the id — never an empty menu entry (ADR 0543: an unlabelled affordance is worse than none).
    private static string NameOf(Dictionary<string, string> names, string? id)
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return names.FirstOrDefault(pair => string.Equals(pair.Key, culture, StringComparison.OrdinalIgnoreCase)).Value
            ?? names.FirstOrDefault(pair => string.Equals(pair.Key, "en", StringComparison.OrdinalIgnoreCase)).Value
            ?? names.Values.FirstOrDefault()
            ?? id
            ?? string.Empty;
    }

    private sealed record BrandResource
    {
        public string? PackId { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public string? PaletteId { get; set; }

        public bool CanChoosePalette { get; set; }

        public List<PaletteResource> Palettes { get; set; } = [];

        public Dictionary<string, Dictionary<string, Dictionary<string, string>>>? Theme { get; set; }

        public string? Version { get; set; }

        public List<LinkResponse> Links { get; set; } = [];
    }

    private sealed record PaletteResource
    {
        public string? Id { get; set; }

        public Dictionary<string, string> Name { get; set; } = new();
    }
}
