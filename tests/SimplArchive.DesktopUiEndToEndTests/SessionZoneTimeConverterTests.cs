using System.Globalization;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop's reminder dialog and My Work list show RemindAt on the SESSION's clock (#1911), as the web does —
// not in the offset the server answered with (UTC on Postgres). The zone is pinned per converter, never via the
// process-wide session zone, so this runs beside any other test.
public class SessionZoneTimeConverterTests
{
    private static readonly TimeZoneInfo Zurich =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Zurich");

    [Theory]
    // As Postgres hands it back: 07:00Z in summer is 09:00 in Zurich, and 08:00Z after the October change is too.
    [InlineData("2026-10-24T07:00:00+00:00", "10/24/2026 09:00")]
    [InlineData("2026-10-26T08:00:00+00:00", "10/26/2026 09:00")]
    // Whatever offset it arrived in, the reading is Zurich's.
    [InlineData("2026-10-26T03:00:00-05:00", "10/26/2026 09:00")]
    public void An_instant_is_shown_on_the_session_clock_not_in_the_payloads_offset(string instant, string expected)
    {
        var converter = new SessionZoneTimeConverter(() => Zurich);

        var shown = converter.Convert(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, shown);
    }

    [Fact]
    public void Nothing_bound_shows_nothing()
    {
        Assert.Equal(string.Empty, new SessionZoneTimeConverter(() => Zurich).Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
