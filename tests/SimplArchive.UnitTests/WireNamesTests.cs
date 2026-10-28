using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The brand never changes the wire protocol (ADR 0987): what a client or a server sends is fixed, so a white-labelled
// build talks to every client unchanged. The build's name (ProductBuild) is a TEXT, read only by Localization, which
// hands it on as Strings.ProductName; a wire identifier taking it would move with the brand.
public class WireNamesTests
{
    [Fact]
    public void The_wire_names_are_fixed()
    {
        Assert.Equal("alcifra", WireNames.Product);
        Assert.Equal("webdav", WireNames.WebDavSegment);
        Assert.Equal("/webdav", SimplArchive.Api.WebDav.WebDavMiddleware.BasePath);
    }

    [Fact]
    public void Only_Localization_reads_the_builds_name()
    {
        var root = RepoPaths.Root();
        string[] kinds = [".cs", ".razor", ".cshtml", ".axaml"];
        var readers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(f => kinds.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)
                && !f.StartsWith("src/SimplArchive.Localization/", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(Path.Combine(root, f)).Contains("ProductBuild", StringComparison.Ordinal))
            .ToList();

        Assert.True(readers.Count == 0,
            "These files read the build's name; a text uses Strings.ProductName, a wire identifier WireNames (ADR 0987):\n  "
            + string.Join("\n  ", readers));
    }
}
