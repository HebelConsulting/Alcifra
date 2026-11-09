using Alcifra.ApiClient.PasswordStore;
using Alcifra.Pass.Infrastructure;

namespace Alcifra.UnitTests;

/// <summary>
/// What <c>apass cicd apply</c> sets (ADR 1084): only what a target lacks or holds older than the secret's last change, and
/// a variable whose value differs. Decided from plain index data and what the platform shows, so no key is involved.
/// </summary>
public sealed class CiCdPlanTests
{
    private static readonly DateTimeOffset Changed = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static (string, CiCdIndexInfo) Signing() => ("Signing", new CiCdIndexInfo(
        ["github:o/public", "github:o/edition"], ["MACOS_CERT_PASSWORD", "APPLE_TEAM_ID"], [], ["POSTPONED=false"])
    { ChangedAt = Changed });

    [Fact]
    public void A_secret_the_target_lacks_is_missing_one_it_holds_older_is_stale_and_a_newer_one_is_unchanged()
    {
        var targets = new Dictionary<string, CiCdTargetState>
        {
            ["github:o/public"] = new(
                new Dictionary<string, DateTimeOffset> { ["MACOS_CERT_PASSWORD"] = Changed.AddDays(-1), ["APPLE_TEAM_ID"] = Changed.AddDays(1) },
                new Dictionary<string, string> { ["POSTPONED"] = "false" }),
            ["github:o/edition"] = new(new Dictionary<string, DateTimeOffset>(), new Dictionary<string, string> { ["POSTPONED"] = "true" }),
        };

        var steps = CiCdPlan.For([Signing()], targets);

        Assert.Equal(CiCdKeyState.Stale, State(steps, "MACOS_CERT_PASSWORD", "github:o/public"));
        Assert.Equal(CiCdKeyState.Unchanged, State(steps, "APPLE_TEAM_ID", "github:o/public"));
        Assert.Equal(CiCdKeyState.Unchanged, State(steps, "POSTPONED", "github:o/public"));
        // The 2026-11-08 case: the second repository lacks two secrets.
        Assert.Equal(CiCdKeyState.Missing, State(steps, "MACOS_CERT_PASSWORD", "github:o/edition"));
        Assert.Equal(CiCdKeyState.Missing, State(steps, "APPLE_TEAM_ID", "github:o/edition"));
        Assert.Equal(CiCdKeyState.Stale, State(steps, "POSTPONED", "github:o/edition"));
    }

    [Fact]
    public void Names_match_without_regard_to_case_as_the_platform_stores_them_upper_cased()
    {
        var targets = new Dictionary<string, CiCdTargetState>
        {
            ["github:o/public"] = new(
                new Dictionary<string, DateTimeOffset> { ["macos_cert_password"] = Changed.AddDays(1), ["apple_team_id"] = Changed.AddDays(1) },
                new Dictionary<string, string> { ["postponed"] = "false" }),
        };

        Assert.All(CiCdPlan.For([Signing()], targets, onlyTarget: "github:o/public"), s => Assert.Equal(CiCdKeyState.Unchanged, s.State));
    }

    [Fact]
    public void One_target_asked_for_plans_that_target_alone() =>
        Assert.All(CiCdPlan.For([Signing()], new Dictionary<string, CiCdTargetState>(), onlyTarget: "github:o/edition"),
            s => Assert.Equal("github:o/edition", s.Target));

    private static CiCdKeyState State(IReadOnlyList<CiCdStep> steps, string key, string target) =>
        steps.Single(s => s.Key == key && s.Target == target).State;
}
