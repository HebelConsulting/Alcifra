namespace Alcifra.UnitTests;

// The #2304 rules, pinned where they are enforceable:
//
//  1. A SEGMENT key (tenants/{t}/{segment}/…) is built only by the one factory (ContentSegmentKeys). A writer that
//     builds one itself is a second layout definition — the drift this guards against.
//  2. The ENFORCEMENT point exists: DocumentFinalizer re-homes every uploaded version under the segment its mask
//     declares, because many doors key their bytes before the mask is assigned (a new WebDAV name, an intray filing,
//     an import). The behavioural half lives in SegmentedContentKeysTests (E2E).
//
// Why not forbid every direct ObjectKeyBuilder.Build call: fifteen doors key content before classification names the
// mask, so such a rule would be an allowlist of the whole codebase. The guarantee is the finalizer's, like the
// SaveChanges invariants — one enforcement point, not thirty checks.
public class ContentKeysHaveOneFactoryTests
{
    [Fact]
    public void Only_the_one_factory_builds_segment_keys()
    {
        var root = RepoPaths.Root();
        const string TheFactory = "ModuleContentKeys.cs";   // ContentSegmentKeys, where ModuleContentKey belongs

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) is not (TheFactory or "ObjectKeyBuilder.cs"))
            .Where(f => File.ReadAllText(f).Contains("ObjectKeyBuilder.ModuleContentKey(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A content-segment key must be built through ContentSegmentKeys.ContentKey / ContentKeyAsync (#2304), so "
            + "the segment layout has one definition. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_finalizer_re_homes_a_version_after_classification()
    {
        var finalizer = Path.Combine(RepoPaths.Root(), "src", "Alcifra.Api", "Documents", "DocumentFinalizer.cs");
        var text = File.ReadAllText(finalizer);

        // The call must come AFTER classification (the mask is what decides the segment): both the staged and the
        // auto-classified branches precede it.
        var classify = text.IndexOf("await AutoClassifyAsync(", StringComparison.Ordinal);
        var enforce = text.IndexOf("await EnsureSegmentKeyAsync(version", StringComparison.Ordinal);
        Assert.True(classify >= 0, "DocumentFinalizer no longer classifies a version through AutoClassifyAsync.");
        Assert.True(enforce > classify,
            "DocumentFinalizer.EnsureSegmentKeyAsync must run AFTER classification — it is the one enforcement point "
            + "that files a version under the mask's content segment (#2304).");
    }
}
