using System.Text.Json;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// The contact card both clients read and save (SimplArchiveMobile #46; moved out of the desktop's form).
public sealed class ContactCardTests
{
    [Fact]
    public void A_card_reads_what_the_server_sent_and_sends_back_without_empty_rows()
    {
        var body = JsonDocument.Parse("""
            {"givenName":"Anna","familyName":"Muster","formattedName":"Anna Muster",
             "emails":[{"value":"anna@demo.test","type":"home"},{"value":"  "}],
             "phones":[{"value":"+41 41 000 00 00"}],
             "addresses":[{"street":"Seestrasse 4","city":"Luzern"},{"type":"work"}]}
            """).RootElement;

        var card = ContactCard.From(body);
        Assert.Equal(("Anna", "Muster", "Anna Muster"), (card.GivenName, card.FamilyName, card.StoredFormattedName));
        Assert.Equal("mobile", card.Phones.Single().Type);                    // the default type, as the desktop used
        Assert.Equal("home", card.Addresses[0].Type);

        var sent = JsonSerializer.SerializeToElement(card.ToPayload());
        Assert.Single(sent.GetProperty("emails").EnumerateArray());            // the blank one is dropped
        Assert.Single(sent.GetProperty("addresses").EnumerateArray());         // the empty one is dropped
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("organization").ValueKind);
    }
}
