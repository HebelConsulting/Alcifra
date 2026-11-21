using System.Security.Cryptography;
using System.Text.Json;
using Alcifra.Application.Branding;
using Alcifra.Theming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alcifra.Infrastructure.Branding;

/// <summary>
/// Loads the runtime brand packs from <see cref="BrandPacksOptions.Directory"/> once, at startup (ADR 1103, #2408).
/// </summary>
/// <remarks>
/// A pack that fails to parse is LEFT OUT with a Warning naming it and its reason: silently rendering half a brand
/// is worse than falling back to the installation's own, which every page can paint. The version is a hash over the
/// pack's files, so a pack replaced on disk at deploy time reaches the clients as a new version and re-caches —
/// nothing about a pack is cached across a restart.
/// </remarks>
public sealed class DirectoryBrandPackStore : IBrandPackStore
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".css"] = "text/css",
        [".woff2"] = "font/woff2",
    };

    private readonly ILogger<DirectoryBrandPackStore> _logger;
    private readonly Dictionary<string, (BrandPack Pack, string Directory)> _packs = new(StringComparer.Ordinal);

    public DirectoryBrandPackStore(IOptions<BrandPacksOptions> options, ILogger<DirectoryBrandPackStore> logger)
    {
        _logger = logger;
        var root = options.Value.Directory;
        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogDebug("No brand packs configured ({Key} is empty) — tenants wear the installation's own brand", BrandPacksOptions.DirectoryKey);
            return;
        }

        if (!System.IO.Directory.Exists(root))
        {
            _logger.LogWarning("The configured brand pack directory {Directory} does not exist — tenants wear the installation's own brand", root);
            return;
        }

        foreach (var directory in System.IO.Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);
            try
            {
                var pack = Load(id, directory);
                _packs[id] = (pack, directory);
                _logger.LogInformation("Loaded brand pack {PackId} ({ProductName}, {Palettes} palette(s), version {Version})", pack.Id, pack.ProductName, pack.ColorVariants.Count, pack.Version);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                or UnauthorizedAccessException or BrandPaletteInvalidException)
            {
                _logger.LogWarning(ex, "Brand pack {PackId} in {Directory} was left out — it does not load", id, directory);
            }
        }

        AvailablePackIds = _packs.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> AvailablePackIds { get; } = [];

    public BrandPack? Find(string? packId) =>
        packId is not null && _packs.TryGetValue(packId, out var found) ? found.Pack : null;

    public BrandAssetSource? Open(string packId, string assetName)
    {
        if (!_packs.TryGetValue(packId, out var found)
            || !found.Pack.Assets.TryGetValue(assetName, out var file)
            || Path.GetFileName(file) != file)
        {
            return null;
        }

        var path = Path.Combine(found.Directory, file);
        if (!File.Exists(path))
        {
            return null;
        }

        var contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        return new BrandAssetSource(File.OpenRead(path), contentType);
    }

    private static BrandPack Load(string id, string directory)
    {
        var manifestPath = Path.Combine(directory, "pack.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;

        var productName = root.GetProperty("productName").GetString();
        if (string.IsNullOrWhiteSpace(productName))
        {
            throw new InvalidOperationException("pack.json has no productName");
        }

        var colors = ReadPalette(root.GetProperty("colors"), id: null);
        var variants = new List<BrandPalette>();
        if (root.TryGetProperty("colorVariants", out var variantsElement))
        {
            foreach (var variant in variantsElement.EnumerateObject())
            {
                variants.Add(ReadPalette(variant.Value, variant.Name));
            }
        }

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var assetsElement))
        {
            foreach (var asset in assetsElement.EnumerateObject())
            {
                var file = asset.Value.GetString();
                if (!string.IsNullOrWhiteSpace(file))
                {
                    assets[asset.Name] = file;
                }
            }
        }

        // Every palette has to be PAINTABLE, held to the same parser and the same contrast standard as every other
        // palette in the product (ADR 1103): a missing colour, a value that is not a colour, or a pair that fails
        // WCAG AA is refused HERE — the pack is left out with a Warning naming it, rather than painting half a
        // brand on a client or failing an external-link landing page a stranger is reading.
        foreach (var palette in variants.Prepend(colors))
        {
            var tokens = ThemeTokensReader.FromGroups(productName, palette.Light.Groups, palette.Dark.Groups);
            var failures = ThemeTokensReader.Validate(tokens).ToList();
            if (failures.Count > 0)
            {
                throw new BrandPaletteInvalidException(
                    $"Palette '{palette.Id ?? "(default)"}' cannot be painted: {string.Join("; ", failures)}");
            }
        }

        return new BrandPack(id, productName, colors, variants, assets, VersionOf(directory));
    }

    private static BrandPalette ReadPalette(JsonElement element, string? id)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in nameElement.EnumerateObject())
            {
                names[name.Name] = name.Value.GetString() ?? string.Empty;
            }
        }

        return new BrandPalette(
            id,
            names,
            ReadTheme(element.GetProperty("light")),
            ReadTheme(element.GetProperty("dark")));
    }

    private static BrandTheme ReadTheme(JsonElement element)
    {
        var groups = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in element.EnumerateObject())
        {
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in group.Value.EnumerateObject())
            {
                tokens[token.Name] = token.Value.GetString() ?? string.Empty;
            }

            groups[group.Name] = tokens;
        }

        return new BrandTheme(groups);
    }

    /// <summary>The pack's version: a hash over every file in its directory, so a changed asset is a new version.</summary>
    private static string VersionOf(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
