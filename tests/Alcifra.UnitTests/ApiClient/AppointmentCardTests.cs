using System.Text.Json;
using Alcifra.ApiClient;

namespace Alcifra.UnitTests.ApiClient;

// The appointment both clients read and save (SimplArchiveMobile #47; moved out of the desktop's form).
public sealed class AppointmentCardTests
{
    [Fact]
    public void A_card_reads_wall_clocks_and_zones_and_sends_them_back_without_an_offset_and_with_its_rule()
    {
        var body = JsonDocument.Parse("""
            {"summary":"Board","start":"2026-10-15T09:00:00","end":"2026-10-15T10:30:00","timeZoneId":"Europe/Zurich",
             "recurrenceRule":"FREQ=WEEKLY","attendees":[{"name":"Anna","address":"anna@demo.test","status":"ACCEPTED"}]}
            """).RootElement;

        var card = AppointmentCard.From(body);
        Assert.Equal(("Europe/Zurich", "Europe/Zurich"), (card.StartTimeZoneId, card.EndTimeZoneId));   // the old single zone fills both
        Assert.Equal(new DateTime(2026, 10, 15, 9, 0, 0), card.Start);
        Assert.Single(card.Attendees);

        var sent = JsonSerializer.SerializeToElement(card.ToPayload("this", "2026-10-15T07:00:00Z"));
        Assert.Equal("2026-10-15T09:00:00", sent.GetProperty("start").GetString());      // no offset: the zone travels apart
        Assert.Equal("FREQ=WEEKLY", sent.GetProperty("recurrenceRule").GetString());       // never dropped
        Assert.Equal("this", sent.GetProperty("scope").GetString());
    }

    [Fact]
    public void A_new_entry_starts_on_the_next_full_hour_and_runs_an_hour()
    {
        var card = AppointmentCard.NewAt(new DateTime(2026, 10, 15, 14, 20, 0), "Europe/Zurich");
        Assert.Equal((new DateTime(2026, 10, 15, 15, 0, 0), new DateTime(2026, 10, 15, 16, 0, 0)), (card.Start, card.End));
        Assert.Equal("Europe/Zurich", card.StartTimeZoneId);
    }
}
