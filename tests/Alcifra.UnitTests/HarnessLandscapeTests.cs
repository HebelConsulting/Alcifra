namespace Alcifra.UnitTests;

// The harness landscape (docs/harnesses/harnesses.typ, #2397) is held true the way the ADR index and the SeedCLAUDE
// lockstep are:
//
//  1. Every project under tests/ and tools/ must appear in it — a new harness cannot land silently.
//  2. The private tools carry the not-portable posture: the section is behind the site gate, so the website extract
//     (the same source, `--input site=true`) cannot publish what describes tools/ (ADR 0484). The bundle's own
//     validation refuses a leak; this is the source-level half, where the mistake would be made.
public class HarnessLandscapeTests
{
    // This file is PUBLISHED (tests/), but the document and the tools/ tree it checks are WITHHELD from the mirror
    // (ADR 0484) — so on the mirror the guard has no input. Gated exactly as AdrIndexTests and SeedClaudeLockstepTests
    // are; without this the publish workflow's staged-tree gate refuses to push the mirror, which is how the omission
    // was found (the v0.62.4 sync after #2400).
    private static bool Private() =>
        PrivateRepositoryGate.RepoRoot() is { } root && PrivateRepositoryGate.IsPrivateRepository(root);

    private static string Document()
    {
        var path = Path.Combine(RepoPaths.Root(), "docs", "harnesses", "harnesses.typ");
        Assert.True(File.Exists(path), $"The harness document is missing: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Every_project_under_tests_and_tools_appears_in_the_document()
    {
        if (!Private())
        {
            return; // the public mirror has neither docs/ nor tools/, by design
        }

        var root = RepoPaths.Root();
        var text = Document();

        var projects = new[] { "tests", "tools" }
            .SelectMany(tree => Directory.EnumerateFiles(Path.Combine(root, tree), "*.csproj", SearchOption.AllDirectories))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !name.Contains(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // A guard on the guard: the enumeration must actually see the trees, or it would pass over an empty list.
        Assert.True(projects.Count >= 15, $"Only {projects.Count} projects found under tests/ and tools/ — the enumeration is wrong.");

        // The document names a harness the way a reader does — "UiEndToEndTests" for Alcifra.UiEndToEndTests — so
        // either spelling satisfies it. The full name is what a project file carries; the short one is the prose.
        static bool Named(string text, string project) =>
            text.Contains(project, StringComparison.Ordinal)
            || (project.StartsWith("Alcifra.", StringComparison.Ordinal)
                && text.Contains(project["Alcifra.".Length..], StringComparison.Ordinal));

        var missing = projects.Where(name => !Named(text, name)).ToList();
        Assert.True(missing.Count == 0,
            "Every harness project must appear in docs/harnesses/harnesses.typ (#2397), so the landscape stays the one "
            + "document describing it. Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_private_tools_section_sits_behind_the_site_gate()
    {
        if (!Private())
        {
            return; // the public mirror has no docs/, by design
        }

        var text = Document();

        // The gate itself: the extract compiles the same source with --input site=true.
        Assert.Contains("sys.inputs.at(\"site\"", text, StringComparison.Ordinal);

        var marker = text.IndexOf("// not-portable:", StringComparison.Ordinal);
        Assert.True(marker >= 0,
            "The private tools section must carry the `// not-portable: <title> — <reason>` marker (the SeedCLAUDE posture).");

        var gate = text.IndexOf("#if not site [", marker, StringComparison.Ordinal);
        Assert.True(gate > marker, "The not-portable marker must be followed by the `#if not site [` gate.");

        // The private section's heading exists only inside that gate: the extract leaves the section out entirely.
        var heading = text.IndexOf("= Private tools", StringComparison.Ordinal);
        Assert.True(heading > gate,
            "The private tools section must sit INSIDE `#if not site`, so the website extract cannot carry it (ADR 0484).");

        // This file is PUBLISHED (tests/), so it may not name the private projects itself — the brand gate in
        // PublicMirrorBoundaryTests is right to refuse that. The names live in the document (docs/ is withheld) and
        // in the project files the first test enumerates; the GATING is what this test can check without them.
    }
}
