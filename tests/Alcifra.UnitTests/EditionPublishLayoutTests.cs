using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.UnitTests;

// What `alcifra edition publish` reads (Licensing ADR 0022): a release needs what a manifest names to arrive before the
// manifest, so children come before an index, and the release's own version also carries `latest`.
public sealed class EditionPublishLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("edition-layout").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Layout(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(directory, "blobs", "sha256"));
        return directory;
    }

    private static string Blob(string layout, byte[] bytes)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(layout, "blobs", "sha256", hex), bytes);
        return $"sha256:{hex}";
    }

    private static string Json(string layout, object value) => Blob(layout, JsonSerializer.SerializeToUtf8Bytes(value));

    private static void Index(string layout, params (string Digest, string? Tag)[] entries) =>
        File.WriteAllText(Path.Combine(layout, "index.json"), JsonSerializer.Serialize(new
        {
            manifests = entries.Select(e => e.Tag is null
                ? (object)new { digest = e.Digest }
                : new { digest = e.Digest, annotations = new Dictionary<string, string> { ["org.opencontainers.image.ref.name"] = e.Tag } }),
        }));

    [Fact]
    public void A_child_manifest_comes_before_the_index_and_the_version_also_carries_latest()
    {
        var layout = Layout("easyname-archiv");
        var config = Blob(layout, Encoding.UTF8.GetBytes("config"));
        var layer = Blob(layout, Encoding.UTF8.GetBytes("layer"));
        var child = Json(layout, new { config = new { digest = config }, layers = new[] { new { digest = layer, mediaType = "application/x" } } });
        var index = Json(layout, new { mediaType = "application/vnd.oci.image.index.v1+json", manifests = new[] { new { digest = child } } });
        Index(layout, (index, "0.57.6"));

        var read = OciLayout.Read(layout, "0.57.6");

        Assert.Equal([config, layer], read.Blobs.Select(b => b.Digest));
        Assert.Equal("application/x", read.Blobs[1].MediaType);
        Assert.Equal(
            [(child, null), (index, null), (index, "0.57.6"), (index, "latest")],
            read.Manifests.Select(m => (m.Digest, m.Tag)));
        Assert.All(read.Manifests, m => Assert.Equal("easyname-archiv", m.Repository));
        Assert.Equal("application/vnd.oci.image.index.v1+json", read.Manifests[2].MediaType);
    }

    [Fact]
    public void Another_tag_such_as_a_signature_stays_as_it_is_and_earns_no_latest()
    {
        var layout = Layout("easyname-archiv");
        var manifest = Json(layout, new { schemaVersion = 2 });
        Index(layout, (manifest, "sha256-abc.sig"));

        Assert.Equal(["sha256-abc.sig"], OciLayout.Read(layout, "0.57.6").Manifests.Select(m => m.Tag).OfType<string>());
    }

    [Fact]
    public void A_double_underscore_in_the_folder_name_is_a_slash_in_the_repository()
    {
        var layout = Layout("charts__easyname-archiv");
        Index(layout, (Json(layout, new { schemaVersion = 2 }), "0.57.6"));

        Assert.All(OciLayout.Read(layout, "0.57.6").Manifests, m => Assert.Equal("charts/easyname-archiv", m.Repository));
    }

    [Fact]
    public void A_layer_the_layout_does_not_hold_is_named_in_the_refusal()
    {
        var layout = Layout("easyname-archiv");
        var missing = $"sha256:{new string('a', 64)}";
        Index(layout, (Json(layout, new { layers = new[] { new { digest = missing } } }), "0.57.6"));

        Assert.Contains(missing, Assert.Throws<CliException>(() => OciLayout.Read(layout, "0.57.6")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("easyname-Archiv-0.57.6-arm64.dmg", "macos-arm64", "client")]
    [InlineData("easyname-Archiv-0.57.6-x64.dmg", "macos-x64", "client")]
    [InlineData("easyname-Archiv-0.57.6-win-x64.zip", "windows-x64", "client")]
    [InlineData("easyname-Archiv-0.57.6-linux-x64.tar.gz", "linux-x64", "client")]
    [InlineData("easyname-Archiv-Manual.pdf", "any", "manual")]
    public void A_release_file_for_a_person_says_what_it_is(string file, string platform, string kind) =>
        Assert.Equal((platform, kind), EditionDownloads.Classify(file));

    [Theory]
    [InlineData("easyname-archiv-0.57.6.tgz")]
    [InlineData("site-bundle.zip")]
    public void What_is_not_for_a_person_is_left_out(string file) => Assert.Null(EditionDownloads.Classify(file));
}
