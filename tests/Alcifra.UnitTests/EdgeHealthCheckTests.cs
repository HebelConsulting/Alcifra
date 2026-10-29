using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// The edge in front of the kiosk and the vendor stack must ride out ONE failed readiness check (#1868, ADR 0956).
//
// The host they share stalls for 2-3 s at a time, every container at once. With Caddy's default of one failure, a
// single stall marked both demo instances down at the same moment and every request in the next interval was answered
// 503, although nothing was down; with the vendor's single upstream, any stall did the same. Two consecutive failures
// ride out one stall, and a timeout of the app's own probe budget (3 s, ADR 0807) lets the check hear the answer the
// app's 2 s connect timeout produces instead of abandoning it.
//
// tools/ is withheld from the public mirror (ADR 0484), so this runs only in the private repository.
public class EdgeHealthCheckTests
{
    [Fact]
    public void Every_app_upstream_needs_two_failed_checks_and_the_full_probe_budget()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root))
        {
            return;
        }

        var path = Path.Combine(root, "tools", "edge", "Caddyfile");
        Assert.True(File.Exists(path), "tools/edge/Caddyfile has moved, so this guard stopped watching it.");

        // Every block that health-checks the app's readiness endpoint, and nothing else (the push servers check
        // their own health path on their own terms).
        var blocks = Regex.Matches(File.ReadAllText(path), @"\{[^{}]*health_uri /health/ready[^{}]*\}")
            .Select(m => m.Value)
            .ToList();
        Assert.True(blocks.Count >= 2, "Expected the demo's upstream snippet and the vendor's upstream to health-check /health/ready.");

        foreach (var block in blocks)
        {
            var fails = Regex.Match(block, @"health_fails\s+(\d+)");
            Assert.True(fails.Success && int.Parse(fails.Groups[1].Value) >= 2,
                $"An app upstream marks an instance down after ONE failed check:\n{block}\n\nOne host stall then takes "
                + "every instance out at once and the edge answers 503 for the next interval (#1868). Keep health_fails 2.");

            var timeout = Regex.Match(block, @"health_timeout\s+(\d+)s");
            Assert.True(timeout.Success && int.Parse(timeout.Groups[1].Value) >= 3,
                $"An app upstream gives the readiness check less than the 3 s probe budget:\n{block}\n\nThe app's own "
                + "connect timeout is 2 s (ADR 0807), so a shorter edge timeout abandons the answer it is about to give.");
        }
    }
}
