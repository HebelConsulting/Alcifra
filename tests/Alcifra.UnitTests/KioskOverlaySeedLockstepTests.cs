using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// The chart's kiosk overlay (charts/alcifra/values-kiosk.yaml) seeds the same demo administrator the live kiosk's compose
// stack does (tools/kiosk/docker-compose.yml). Nothing compared them, and the overlay lacked the administrator's display
// name, which the server requires (CK_Users_DisplayName_NotBlank): an install from the overlay failed at its first start,
// while the live demo, which runs the compose stack, never showed it. Found installing the easyname edition on minikube
// (2026-11-08). Every Demo__Administrator__* key the compose stack sets must be set by the overlay too.
public partial class KioskOverlaySeedLockstepTests
{
    [Fact]
    public void The_overlay_seeds_every_administrator_setting_the_kiosk_does()
    {
        // A root exists in the public mirror too; only the private repository HAS tools/ (ADR 0484). Asking for a root
        // alone failed every mirror run from 2026-11-08 on, and nothing published until it was fixed.
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
        {
            return;
        }

        var compose = ComposeKey().Matches(File.ReadAllText(Path.Combine(root, "tools", "kiosk", "docker-compose.yml")))
            .Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
        var overlay = OverlayKey().Matches(File.ReadAllText(Path.Combine(root, "charts", "alcifra", "values-kiosk.yaml")))
            .Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(compose);
        Assert.Empty(compose.Except(overlay).Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"^\s+(?<key>Demo__Administrator__\w+):", RegexOptions.Multiline)]
    private static partial Regex ComposeKey();

    [GeneratedRegex(@"^\s+- name: (?<key>Demo__Administrator__\w+)\s*$", RegexOptions.Multiline)]
    private static partial Regex OverlayKey();
}
