using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplArchive.Theming;

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
    [property: JsonPropertyName("colors")] BrandColors Colors)
{
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
        typeof(Brand).Assembly.GetManifestResourceStream("SimplArchive.Theming.brand.json")
        ?? throw new InvalidOperationException(
            "brand.json is missing from SimplArchive.Theming. It is the shipped brand and is embedded deliberately, so "
            + "this means the build no longer includes brand/brand.json as an EmbeddedResource.");
}

/// <summary>The brand's palettes, in the shape the theme uses.</summary>
public sealed record BrandColors(
    [property: JsonPropertyName("light")] ThemePalette Light,
    [property: JsonPropertyName("dark")] ThemePalette Dark);
