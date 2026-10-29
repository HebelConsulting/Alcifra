using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcifra.Theming;

/// <summary>
/// The product's brand as the build ships it (ADR 0989): <c>brand/brand.json</c>, the ONE file that says what a person
/// reads about the product and how it looks. A white-label build changes that file and nothing else. Wire names are
/// not part of it: they never follow the brand (ADR 0987).
/// </summary>
/// <remarks>
/// The file is EMBEDDED, as the theme's tokens were: the shipped brand has to exist without any file on any
/// filesystem, and the build-time generators and the runtime read the very same document. It is read strictly: a
/// field this record does not know is refused, so a misspelt key fails the build's tests rather than being ignored.
/// </remarks>
public sealed record Brand(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fileStem")] string FileStem,
    [property: JsonPropertyName("publisher")] string Publisher,
    [property: JsonPropertyName("credit")] string? Credit,
    [property: JsonPropertyName("colors")] BrandColors Colors,
    [property: JsonPropertyName("fonts")] BrandFonts Fonts,
    [property: JsonPropertyName("wordmark")] BrandWordmark Wordmark,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("distribution")] BrandDistribution Distribution,
    [property: JsonPropertyName("imprint")] BrandImprint Imprint)
{
    /// <summary>
    /// Whether the product shows the brand's own name, so its wordmark may stand for it (ADR 0992). An installation
    /// that renamed the product (<c>Branding:ProductName</c>) keeps its name as text: the mark spells another name.
    /// </summary>
    public static bool ShowsWordmark(string productName) => string.Equals(productName, Shipped.Name, StringComparison.Ordinal);

    /// <summary>
    /// The served manual's file name (ADR 0997): the brand's file stem, so a downloaded manual says whose it is.
    /// </summary>
    [JsonIgnore]
    public string ManualFileName => $"{FileStem}-Manual.pdf";

    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly Lazy<Brand> ShippedBrand = new(() => Read(OpenShipped()));

    /// <summary>The brand this build ships.</summary>
    public static Brand Shipped => ShippedBrand.Value;

    /// <summary>Reads a brand file, refusing unknown and missing fields.</summary>
    public static Brand Read(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        // The schema reference is for editors; it is not part of the brand.
        var fields = document.RootElement.EnumerateObject().Where(p => p.Name != "$schema")
            .ToDictionary(p => p.Name, p => p.Value);
        return JsonSerializer.Deserialize<Brand>(JsonSerializer.Serialize(fields), Options)
            ?? throw new InvalidOperationException("The brand file is empty.");
    }

    private static Stream OpenShipped() =>
        typeof(Brand).Assembly.GetManifestResourceStream("Alcifra.Theming.brand.json")
        ?? throw new InvalidOperationException(
            "brand.json is missing from Alcifra.Theming. It is the shipped brand and is embedded deliberately, so "
            + "this means the build no longer includes brand/brand.json as an EmbeddedResource.");
}

/// <summary>The brand's palettes, in the shape the theme uses.</summary>
public sealed record BrandColors(
    [property: JsonPropertyName("light")] ThemePalette Light,
    [property: JsonPropertyName("dark")] ThemePalette Dark);

/// <summary>The brand's typefaces, by family name; their files live in <c>brand/assets/fonts</c> (ADR 0991).</summary>
public sealed record BrandFonts(
    [property: JsonPropertyName("ui")] string Ui,
    [property: JsonPropertyName("document")] string Document)
{
    /// <summary>
    /// The stem a family's files are named by: the family without spaces, so "Source Sans 3" is
    /// <c>SourceSans3-Regular.ttf</c>, <c>SourceSans3Variable.woff2</c> and <c>SourceSans3-LICENSE.txt</c>.
    /// </summary>
    public static string FileStem(string family) => family.Replace(" ", string.Empty);
}

/// <summary>
/// The brand's wordmark (ADR 0992): SVG files relative to <c>brand/assets</c>, one for light backgrounds and one for
/// dark. The API serves <c>brand/assets</c> at <see cref="UrlRoot"/>.
/// </summary>
public sealed record BrandWordmark(
    [property: JsonPropertyName("light")] string Light,
    [property: JsonPropertyName("dark")] string Dark)
{
    /// <summary>Where the API serves <c>brand/assets</c>.</summary>
    public const string UrlRoot = "/brand/";
}

/// <summary>
/// What a build produces is named and published by (ADR 0994). Read by the packaging scripts and publishing
/// workflows through <c>scripts/brand.sh</c>, and here by the desktop's update check and its demo default.
/// </summary>
public sealed record BrandDistribution(
    [property: JsonPropertyName("bundleId")] string BundleId,
    [property: JsonPropertyName("imageRegistry")] string ImageRegistry,
    [property: JsonPropertyName("imageName")] string ImageName,
    [property: JsonPropertyName("chartName")] string ChartName,
    [property: JsonPropertyName("releaseRepository")] string ReleaseRepository,
    [property: JsonPropertyName("domains")] BrandDomains Domains);

/// <summary>The public hosts a build points at (ADR 0994).</summary>
public sealed record BrandDomains(
    [property: JsonPropertyName("demo")] string Demo);

/// <summary>Who publishes this edition (ADR 0995): the publisher's name and address lines, and its support address.</summary>
public sealed record BrandImprint(
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines,
    [property: JsonPropertyName("contact")] string Contact);
