using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Theming;

namespace Alcifra.ApiClient;

/// <summary>
/// The brand the signed-in caller wears (ADR 1103, Mobile #313): their tenant's runtime pack — product name, the
/// palettes it carries, the tokens in force, its declared assets and its version — read through the root's
/// <c>brand</c> rel, which the server advertises only once a tenant is known.
/// </summary>
/// <remarks>
/// <para>
/// The one client of the endpoint for every host that is not the web (the web keeps its own browser-bound state).
/// It is deliberately thin: it READS what the server resolved — the fallback order (user override → tenant palette →
/// the pack's colours → the installation's brand) lives on the server, and a host never re-derives it.
/// </para>
/// <para>
/// <b>Authenticated by construction</b>: it rides the caller's <see cref="ApiCore"/>, so no token means no brand —
/// the endpoint answers 401 and <see cref="ReadAsync"/> answers null, which every host reads as "wear the
/// installation's brand". A tenant with no pack answers with the installation brand and a null
/// <see cref="Brand.PackId"/>, and nothing should be re-painted.
/// </para>
/// <para>
/// What is never cached HERE is the resource: the effective palette differs per tenant and per user, so the small
/// read stays the authority on every sign-in. A host MAY cache the ASSETS by <see cref="Brand.Version"/> (a content
/// hash) — the desktop does, and the phone should.
/// </para>
/// </remarks>
public sealed class TenantBrandClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiCore _core;

    public TenantBrandClient(ApiCore core) => _core = core;

    /// <summary>One palette the brand carries; <c>null</c> id is the pack's own colours.</summary>
    public sealed record BrandPalette(string? Id, IReadOnlyDictionary<string, string> Name);

    /// <summary>An address the brand resource advertised, with the method the server stated for it.</summary>
    public sealed record BrandLink(string Rel, string Href, string Method);

    /// <summary>The caller's effective brand, as the server resolved it.</summary>
    public sealed record Brand(
        string? PackId,
        string ProductName,
        string? PaletteId,
        bool CanChoosePalette,
        IReadOnlyList<BrandPalette> Palettes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? Theme,
        string? Version,
        IReadOnlyList<BrandLink> Links);

    /// <summary>
    /// The caller's brand, or null where there is none to read — no tenant (a platform administrator), an
    /// installation older than the rel, or a read that failed. Null means "the installation's brand stays".
    /// </summary>
    public async Task<Brand?> ReadAsync(CancellationToken cancellationToken = default)
    {
        string href;
        try
        {
            href = await _core.RootHrefAsync("brand", cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        using var response = await _core.Http.GetAsync(href, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var resource = await response.Content.ReadFromJsonAsync<BrandResource>(Json, cancellationToken);
        if (resource is null)
        {
            return null;
        }

        return new Brand(
            resource.PackId,
            resource.ProductName,
            resource.PaletteId,
            resource.CanChoosePalette,
            resource.Palettes
                .Select(palette => new BrandPalette(palette.Id, new Dictionary<string, string>(palette.Name, StringComparer.OrdinalIgnoreCase)))
                .ToList(),
            resource.Theme is { } theme
                ? theme.ToDictionary(
                    outer => outer.Key,
                    outer => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)outer.Value.ToDictionary(
                        middle => middle.Key,
                        middle => (IReadOnlyDictionary<string, string>)middle.Value,
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase)
                : null,
            resource.Version,
            resource.Links
                .Select(link => new BrandLink(link.Rel, link.Href, link.Method))
                .ToList());
    }

    /// <summary>
    /// The tokens the brand's palette carries, or null when there is nothing to paint (the installation's brand, or a
    /// palette the theme reader refuses — which the pack store already refused at load, so this is belt to brace).
    /// </summary>
    public static ThemeTokens? Tokens(Brand brand)
    {
        if (brand.Theme is not { } theme)
        {
            return null;
        }

        try
        {
            return Groups(theme, "light") is { } light && Groups(theme, "dark") is { } dark
                ? ThemeTokensReader.FromGroups(brand.ProductName, light, dark)
                : null;
        }
        catch (BrandPaletteInvalidException)
        {
            return null;
        }
    }

    /// <summary>
    /// One of the pack's DECLARED asset files — the manifest is the whitelist, so a name it does not list answers
    /// null here exactly as the server answers 404. The bytes carry the response's own content type.
    /// </summary>
    public async Task<(byte[] Content, string ContentType)?> ReadAssetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(cancellationToken) is not { } brand)
        {
            return null;
        }

        var href = brand.Links.FirstOrDefault(link =>
            link.Rel == "asset" && link.Href.EndsWith($"/{name}", StringComparison.OrdinalIgnoreCase))?.Href;
        if (href is null)
        {
            return null;
        }

        using var response = await _core.Http.GetAsync(href, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return (
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    /// <summary>
    /// The caller's own palette within the current brand — self-service, and only offered when the resource
    /// advertises <c>select-palette</c> (the tenant allows it and the brand carries a choice). Answers whether the
    /// server accepted it; the caller re-reads the brand afterwards rather than deriving the result itself.
    /// </summary>
    public async Task<bool> SelectPaletteAsync(string? paletteId, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(cancellationToken) is not { } brand
            || brand.Links.FirstOrDefault(link => link.Rel == "select-palette") is not { } link)
        {
            return false;
        }

        using var request = new HttpRequestMessage(
            string.Equals(link.Method, "POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Put,
            link.Href)
        {
            Content = JsonContent.Create(new { paletteId }),
        };

        using var response = await _core.Http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Groups(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> theme,
        string name) =>
        theme.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) is { Value: { } groups }
            ? groups
            : null;

    // The wire shape of BrandController's resource. Private: the hosts read the record above, and a rename on the
    // server should be one place to follow.
    private sealed record BrandResource
    {
        public string? PackId { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public string? PaletteId { get; set; }

        public bool CanChoosePalette { get; set; }

        public List<PaletteResource> Palettes { get; set; } = [];

        public Dictionary<string, Dictionary<string, Dictionary<string, string>>>? Theme { get; set; }

        public string? Version { get; set; }

        public List<LinkResource> Links { get; set; } = [];
    }

    private sealed record PaletteResource
    {
        public string? Id { get; set; }

        public Dictionary<string, string> Name { get; set; } = new();
    }

    private sealed record LinkResource
    {
        public string Rel { get; set; } = string.Empty;

        public string Href { get; set; } = string.Empty;

        public string Method { get; set; } = "GET";
    }
}
