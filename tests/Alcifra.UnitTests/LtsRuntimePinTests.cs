using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// Every .NET base image a Dockerfile FROMs is an LTS line, and it is the line global.json pins.
//
// WHY. Alcifra runs on LONG-TERM-SUPPORT runtimes only (owner, 2026-11-20): an even-numbered .NET release (8,
// 10, 12, …); an odd-numbered (STS) release is never a target, however new. `.github/dependabot.yml` ignores
// the SDK's major updates for that reason — and it was not enough: the docker-images GROUP update #2457 carried
// mcr.microsoft.com/dotnet/sdk 10 -> 11 anyway, alongside two legitimate digest bumps, so a merge would have
// moved the shipped image to an STS runtime while CI building the same tree stayed green (global.json still
// pinned 10.0.301 and the workload installs the SDK from it — the SDK IMAGE was the only thing to move).
//
// WHAT IT ALSO CATCHES, which is the quieter half: the two places that must agree. global.json pins the SDK the
// build uses; the Dockerfile's `sdk:` image is a second, independent statement of the same decision, and
// base/clients' Dockerfile a third. This guard keeps them equal, so a deliberate move to the next LTS is one
// change across all of them — never a dependency bump that moves one.
public sealed partial class LtsRuntimePinTests
{
    /// <summary>A .NET base image reference: `mcr.microsoft.com/dotnet/<repo>:<major>.<minor>…`.</summary>
    [GeneratedRegex(@"mcr\.microsoft\.com/dotnet/(?:sdk|aspnet|runtime):([0-9]+)\.([0-9]+)")]
    private static partial Regex DotnetBaseImage();

    [Fact]
    public void Every_dotnet_base_image_is_the_LTS_major_global_json_pins()
    {
        var root = RepoRoot();
        var sdkVersion = Regex.Match(File.ReadAllText(Path.Combine(root, "global.json")), @"""version""\s*:\s*""([0-9]+)\.");
        Assert.True(sdkVersion.Success, "global.json must pin an SDK version");
        var pinnedMajor = int.Parse(sdkVersion.Groups[1].Value);

        // Every Dockerfile in the repository, at any depth (the app's, the base images' , the OCR sidecar's).
        var dockerfiles = Directory
            .EnumerateFiles(root, "Dockerfile*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(dockerfiles);

        var offending = new List<string>();
        foreach (var dockerfile in dockerfiles)
        {
            foreach (var line in File.ReadLines(dockerfile))
            {
                foreach (Match image in DotnetBaseImage().Matches(line))
                {
                    var major = int.Parse(image.Groups[1].Value);
                    var relative = Path.GetRelativePath(root, dockerfile);
                    if (major % 2 != 0)
                    {
                        offending.Add($"{relative}: {image.Value} — an ODD major is an STS release, never a target");
                    }
                    else if (major != pinnedMajor)
                    {
                        offending.Add($"{relative}: {image.Value} — global.json pins the {pinnedMajor}.x line");
                    }
                }
            }
        }

        Assert.True(offending.Count == 0,
            "A .NET base image disagrees with the LTS-only rule (even-numbered, the global.json line):\n  "
            + string.Join("\n  ", offending));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Alcifra.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
