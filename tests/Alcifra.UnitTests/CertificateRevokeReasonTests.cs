using Alcifra.Cli.Commands;

namespace Alcifra.UnitTests;

/// <summary>
/// <c>alcifra certificates revoke</c> asks why, as the clients do (#2278): one of the reasons, or "other" with words of
/// one's own. Only <c>--on</c>, recording what a certification authority revoked elsewhere, goes without.
/// </summary>
public sealed class CertificateRevokeReasonTests
{
    [Theory]
    [InlineData(null, "lost", null)]
    [InlineData(null, "left", null)]
    [InlineData(null, "other", "Card swapped at the front desk")]
    [InlineData("2026-10-05", null, null)]
    public void A_reason_or_a_recorded_moment_is_enough(string? on, string? reason, string? text) =>
        Assert.Null(CertificateRevokeSettings.ReasonProblem(on, reason, text));

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "misplaced", null)]
    [InlineData(null, "other", null)]
    [InlineData(null, "other", "   ")]
    [InlineData("2026-10-05", "lost", null)]
    public void A_revocation_without_a_reason_is_refused(string? on, string? reason, string? text) =>
        Assert.NotNull(CertificateRevokeSettings.ReasonProblem(on, reason, text));

    [Fact]
    public void Other_text_longer_than_the_server_keeps_is_refused() =>
        Assert.NotNull(CertificateRevokeSettings.ReasonProblem(null, "other", new string('x', 201)));
}
