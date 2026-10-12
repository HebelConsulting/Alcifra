using System.Text.Json;
using SimplArchive.Api.Notifications;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.UnitTests;

// What a phone push carries (SimplArchiveMobile #20, owner decision 2026-10-12): the notification's id and nothing
// else, on every tenant. Web Push's AES-128-GCM over P-256 is not what content should rest on; the phone fetches the
// notification over TLS and keeps it in its own encrypted cache.
public class PhonePushPayloadTests
{
    [Fact]
    public void A_push_carries_the_id_and_nothing_else()
    {
        var item = new PhonePushItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3);

        var payload = JsonDocument.Parse(PhonePushSender.Payload(item)).RootElement;

        Assert.Equal(item.NotificationId, payload.GetProperty("id").GetGuid());
        Assert.Single(payload.EnumerateObject());
    }
}
