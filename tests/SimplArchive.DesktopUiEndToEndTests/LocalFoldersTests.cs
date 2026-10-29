using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop's home folder carries the brand's file stem (ADR 0997), and the folder from before is moved once.
public class LocalFoldersTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("home-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void The_earlier_folder_is_moved_to_the_brands_once()
    {
        Directory.CreateDirectory(Path.Combine(_home, "SimplArchive", "tenant", "user", "temp"));

        var folder = LocalFolders.HomeFolder(_home, "Edition");

        Assert.Equal(Path.Combine(_home, "Edition"), folder);
        Assert.True(Directory.Exists(Path.Combine(folder, "tenant", "user", "temp")));
        Assert.False(Directory.Exists(Path.Combine(_home, "SimplArchive")));
    }

    [Fact]
    public void An_existing_brand_folder_is_kept_and_the_earlier_one_left_alone()
    {
        Directory.CreateDirectory(Path.Combine(_home, "Edition", "mine"));
        Directory.CreateDirectory(Path.Combine(_home, "SimplArchive", "theirs"));

        LocalFolders.HomeFolder(_home, "Edition");

        Assert.True(Directory.Exists(Path.Combine(_home, "Edition", "mine")));
        Assert.True(Directory.Exists(Path.Combine(_home, "SimplArchive", "theirs")));
    }
}
