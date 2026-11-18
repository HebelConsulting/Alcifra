using System.Text.RegularExpressions;
using Alcifra.ApiClient;

namespace Alcifra.UnitTests;

// #2358: Environment.GetFolderPath answers an EMPTY string for a folder that does not exist yet (a fresh Linux account
// without ~/.config), and Path.Combine(string.Empty, …) is a path relative to the current directory — where the CLI's
// token-bearing session file then landed. UserFolders asks with DoNotVerify and refuses what is still empty.
public class UserFoldersTests
{
    // A call, not a mention: the open parenthesis separates it from a doc comment that names the method.
    private static readonly Regex RawCall = new(@"\bGetFolderPath\s*\(", RegexOptions.Compiled);

    private const string TheOneClass = "UserFolders.cs";

    [Fact]
    public void Only_UserFolders_asks_the_platform_for_a_user_folder()
    {
        var root = RepoPaths.Root();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != TheOneClass)
            .Where(f => RawCall.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Environment.GetFolderPath may be called only in src/Alcifra.ApiClient/UserFolders.cs: on its own it "
            + "answers an empty string for a missing folder, which turns every path built on it relative to the "
            + "current directory (#2358). Use UserFolders.Get instead. Offending files:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_still_has_its_path()
    {
        var home = Directory.CreateTempSubdirectory("userfolders-").FullName;
        var saved = (Home: Environment.GetEnvironmentVariable("HOME"), Xdg: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

            var path = UserFolders.Get(Environment.SpecialFolder.ApplicationData);

            Assert.True(Path.IsPathRooted(path), $"expected an absolute path, got '{path}'");
            Assert.False(OperatingSystem.IsLinux() && Directory.Exists(path), "the folder was not meant to exist for this test");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", saved.Home);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", saved.Xdg);
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void An_unanswerable_folder_is_refused_rather_than_made_relative()
    {
        // A folder this platform has no answer for, even with DoNotVerify: the same answer a missing HOME gives. Every
        // platform has some (Linux and macOS lack most of Windows' folders); if one ever had none, nothing is refusable.
        var unanswerable = Enum.GetValues<Environment.SpecialFolder>()
            .Where(f => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify).Length == 0)
            .ToList();
        if (unanswerable.Count == 0)
            return;
        var folder = unanswerable[0];

        var refused = Assert.Throws<UserFolderUnavailableException>(() => UserFolders.Get(folder));

        Assert.Equal(folder, refused.Folder);
    }
}
