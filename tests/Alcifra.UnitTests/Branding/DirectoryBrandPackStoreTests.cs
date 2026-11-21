using System.Text.Json;
using Alcifra.Infrastructure.Branding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Alcifra.UnitTests.Branding;

// The pack directory is deployment configuration, not code (ADR 1103, #2408): a pack that does not parse is LEFT
// OUT (rendering half a brand is worse than the installation's own), an asset can only be one the manifest declares,
// and the version moves when a file does — which is what makes a pack replaced at deploy time re-cache.
public sealed class DirectoryBrandPackStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brand-packs-{Guid.NewGuid():N}");

    public DirectoryBrandPackStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DirectoryBrandPackStore Store() =>
        new(Options.Create(new BrandPacksOptions { Directory = _root }), NullLogger<DirectoryBrandPackStore>.Instance);

    private string WritePack(string id, string productName = "Alcifra Bund", string wordmark = "<svg/>", bool complete = true)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "wordmark-light.svg"), wordmark);
        File.WriteAllText(Path.Combine(directory, "pack.json"), JsonSerializer.Serialize(new
        {
            productName,
            colors = Palette("#101010", "#202020", complete),
            colorVariants = new Dictionary<string, object>
            {
                ["blue"] = new
                {
                    name = new { en = "Blue", de = "Blau" },
                    light = Theme("#1F5FAD", "#FFFFFF", complete),
                    dark = Theme("#1A5193", "#FFFFFF", complete),
                },
            },
            assets = new { wordmarkLight = "wordmark-light.svg" },
        }));
        return directory;
    }

    private static object Palette(string primaryLight, string primaryDark, bool complete) => new
    {
        light = Theme(primaryLight, "#FFFFFF", complete),
        dark = Theme(primaryDark, "#FFFFFF", complete),
    };

    // Every colour of a palette has to be stated (ADR 1103): the pack store refuses a partial one at load. A
    // deliberately incomplete fixture is how the refusal is proven — it states only the primary, as the first
    // version of these fixtures did.
    private static object Theme(string primary, string onPrimary, bool complete) => complete
        ? new
        {
            accent = new
            {
                primary,
                hover = "#0D6A62",
                onPrimary,
                text = "#0B5D56",
                tint = "#E6F2F1",
                selection = "#CCE7E4",
            },
            semantic = new { danger = "#B91C1C", success = "#15803D", warning = "#A16207" },
            neutral = new
            {
                canvas = "#FAFAFC",
                surface = "#FFFFFF",
                sunken = "#F1F2F5",
                hairline = "#E6E7EC",
                text = "#14161C",
                textSecondary = "#5A5F6E",
                textFaint = "#898F9E",
                folder = "#D9A441",
            },
        }
        : new { accent = new { primary, onPrimary } };

    [Fact]
    public void A_well_formed_pack_loads_with_its_name_variants_and_assets()
    {
        WritePack("bund");

        var pack = Store().Find("bund");

        Assert.NotNull(pack);
        Assert.Equal("Alcifra Bund", pack.ProductName);
        Assert.Equal("Blue", pack.ColorVariants.Single().Names["en"]);
        Assert.Equal("#1F5FAD", pack.ColorVariants.Single().Light.Groups["accent"]["primary"]);
        Assert.Equal("wordmark-light.svg", pack.Assets["wordmarkLight"]);
        Assert.False(string.IsNullOrWhiteSpace(pack.Version));
    }

    [Fact]
    public void An_installation_with_no_directory_has_no_packs_and_does_not_throw()
    {
        var store = new DirectoryBrandPackStore(
            Options.Create(new BrandPacksOptions { Directory = null }),
            NullLogger<DirectoryBrandPackStore>.Instance);

        Assert.Empty(store.AvailablePackIds);
        Assert.Null(store.Find("bund"));
    }

    [Fact]
    public void A_broken_pack_is_left_out_while_the_others_load()
    {
        WritePack("bund");
        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "pack.json"), "{ not json");

        var store = Store();

        Assert.Equal(["bund"], store.AvailablePackIds);
        Assert.Null(store.Find("broken"));
    }

    [Fact]
    public void Only_declared_assets_open_and_never_a_path_outside_the_pack()
    {
        WritePack("bund");
        File.WriteAllText(Path.Combine(_root, "bund", "private.txt"), "secret");

        var store = Store();

        var declared = store.Open("bund", "wordmarkLight");
        Assert.NotNull(declared);
        Assert.Equal("image/svg+xml", declared.ContentType);
        declared.Content.Dispose();

        Assert.Null(store.Open("bund", "private.txt"));
        Assert.Null(store.Open("bund", "../outside"));
    }

    [Fact]
    public void The_version_moves_when_a_file_of_the_pack_changes()
    {
        WritePack("bund");
        var before = Store().Find("bund")!.Version;

        WritePack("bund", wordmark: "<svg width='2'/>");

        Assert.NotEqual(before, Store().Find("bund")!.Version);
    }
}
