using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// A card's source section shows the stored vCard or iCalendar verbatim (#2274), except a long PHOTO, whose base64 would
// otherwise fill the pane: it becomes one line naming its size, folded continuation lines and all.
public class StructuredItemSourceTests
{
    [Fact]
    public void A_card_without_a_photo_is_shown_line_for_line()
    {
        const string vcard = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Anna Example\r\nX-CLIENT-SEQ:7\r\nEND:VCARD\r\n";

        Assert.Equal("BEGIN:VCARD\nVERSION:4.0\nFN:Anna Example\nX-CLIENT-SEQ:7\nEND:VCARD\n", StructuredItemSource.Shown(vcard));
    }

    [Fact]
    public void A_long_inline_photo_becomes_one_line_naming_its_stored_size()
    {
        var folds = string.Concat(Enumerable.Repeat("\r\n " + new string('A', 74), 6));
        var photo = $"PHOTO;ENCODING=b;TYPE=JPEG:{new string('/', 46)}{folds}";
        var vcard = $"BEGIN:VCARD\r\nFN:Anna\r\n{photo}\r\nEMAIL:anna@example.com\r\nEND:VCARD";
        var size = System.Text.Encoding.UTF8.GetByteCount(photo);

        Assert.Equal($"BEGIN:VCARD\nFN:Anna\nPHOTO;… ({size} bytes)\nEMAIL:anna@example.com\nEND:VCARD", StructuredItemSource.Shown(vcard));
    }

    [Fact]
    public void A_short_photo_uri_and_a_grouped_long_photo_are_told_apart()
    {
        var shortUri = "PHOTO;MEDIATYPE=image/png:https://example.com/a.png";
        var grouped = $"item1.PHOTO:data:image/png;base64,{new string('B', 300)}";

        var shown = StructuredItemSource.Shown($"BEGIN:VCARD\n{shortUri}\n{grouped}\nEND:VCARD");

        Assert.Equal($"BEGIN:VCARD\n{shortUri}\nPHOTO;… ({grouped.Length} bytes)\nEND:VCARD", shown);
    }

    [Fact]
    public void A_property_that_merely_starts_with_photo_is_left_alone()
    {
        var other = $"PHOTOGRAPHER:{new string('C', 300)}";

        Assert.Equal($"BEGIN:VCARD\n{other}\nEND:VCARD", StructuredItemSource.Shown($"BEGIN:VCARD\n{other}\nEND:VCARD"));
    }

    [Fact]
    public void An_appointment_is_shown_verbatim()
    {
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Review\r\n DESCRIPTION folded\r\nEND:VEVENT\r\nEND:VCALENDAR";

        Assert.Equal(ics.ReplaceLineEndings("\n"), StructuredItemSource.Shown(ics));
    }
}
