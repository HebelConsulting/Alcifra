using Alcifra.Presentation;

namespace Alcifra.UnitTests;

/// <summary>The CI/CD entry rules (ADR 1084) the server, the desktop and apass share.</summary>
public sealed class CiCdEntryRulesTests
{
    [Fact]
    public void The_signing_entry_of_2026_11_08_is_fine()
    {
        var problems = CiCdEntryRules.Problems(
            ["MACOS_CERT_P12_BASE64", "MACOS_CERT_PASSWORD"], ["MACOS_CERT_P12_BASE64"], ["MACOS_NOTARIZATION_POSTPONED=false"],
            ["github:HebelConsulting/Alcifra", "github:HebelConsulting/Alcifra4Easyname@release"]);

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("1ST_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("github_anything")]
    [InlineData("WITH-DASH")]
    [InlineData("")]
    public void A_name_the_platform_refuses_is_refused(string name) =>
        Assert.Equal([CiCdEntryProblem.InvalidKeyName], CiCdEntryRules.Problems([name], [], [], []));

    [Fact]
    public void A_name_used_twice_across_secrets_and_variables_ignoring_case_is_refused() =>
        Assert.Equal([CiCdEntryProblem.DuplicateKeyName], CiCdEntryRules.Problems(["APPLE_TEAM_ID"], [], ["apple_team_id=X"], []));

    [Fact]
    public void A_file_key_must_be_a_secret_key() =>
        Assert.Equal([CiCdEntryProblem.FileKeyNotASecret], CiCdEntryRules.Problems(["A"], ["B"], [], []));

    [Fact]
    public void A_variable_needs_a_name_and_an_equals_sign()
    {
        Assert.Equal([CiCdEntryProblem.InvalidVariable], CiCdEntryRules.Problems([], [], ["NO_VALUE"], []));
        Assert.Equal([CiCdEntryProblem.InvalidVariable], CiCdEntryRules.Problems([], [], ["=value"], []));
        Assert.Equal(("EMPTY", string.Empty), CiCdEntryRules.SplitVariable("EMPTY="));
        Assert.Equal(("URL", "a=b"), CiCdEntryRules.SplitVariable("URL=a=b"));
    }

    [Theory]
    [InlineData("github:HebelConsulting/Alcifra", "HebelConsulting/Alcifra", null)]
    [InlineData("github:HebelConsulting/Alcifra@production", "HebelConsulting/Alcifra", "production")]
    public void A_target_names_a_repository_and_maybe_an_environment(string target, string repository, string? environment) =>
        Assert.Equal((repository, environment), CiCdEntryRules.ParseTarget(target));

    [Theory]
    [InlineData("HebelConsulting/Alcifra")]
    [InlineData("gitlab:group/project")]
    [InlineData("github:HebelConsulting")]
    public void Anything_else_is_no_target(string target) =>
        Assert.Equal([CiCdEntryProblem.InvalidTarget], CiCdEntryRules.Problems([], [], [], [target]));
}
