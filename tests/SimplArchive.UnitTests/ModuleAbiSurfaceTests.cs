using System.Reflection;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

/// <summary>
/// A minor ABI release keeps every public member a published module can call (ADR 0745's compatibility promise). A
/// module is compiled against one ABI and loaded into a newer core, so a member that disappears is a
/// MissingMethodException at module load, after release, not a compile error.
/// </summary>
/// <remarks>
/// It happened in v0.41.0 (ABI 1.14): an OPTIONAL parameter added to <see cref="ModuleRootLink"/> removed its
/// three-argument constructor and deconstruction from the binary, so every 1.13 module declaring a root link was
/// refused on the kiosk, and the Crypto tenant refused to start without its Encryption Module. The snapshot below
/// holds the surface; additions are fine (regenerate with <c>ABI_SURFACE_UPDATE=1</c>), removals fail here.
/// </remarks>
public sealed class ModuleAbiSurfaceTests
{
    private static string SnapshotPath =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ModuleAbi.surface.txt");

    internal static IReadOnlyList<string> Surface()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return [.. typeof(ModuleRootLink).Assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal)
            .SelectMany(t => t.GetMembers(flags).Select(m => $"{t.FullName}::{m}").OrderBy(s => s, StringComparer.Ordinal))];
    }

    [Fact]
    public void No_public_member_a_published_module_may_call_has_disappeared()
    {
        var current = Surface();
        if (Environment.GetEnvironmentVariable("ABI_SURFACE_UPDATE") == "1")
        {
            File.WriteAllLines(SnapshotPath, current);
        }

        var missing = File.ReadAllLines(SnapshotPath).Where(l => l.Length > 0).Except(current, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "These ModuleAbi members existed in a published ABI and are gone, so a module built against it fails to load "
            + "(MissingMethodException). Keep them (an explicit overload, not an optional parameter):\n  "
            + string.Join("\n  ", missing));
    }
}
