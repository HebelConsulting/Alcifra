using System.Net;
using System.Net.Http.Json;

namespace Alcifra.EndToEndTests;

// End-to-end over the real API + Postgres, exercising document reminders (ADR "Document reminders"): a user
// sets a reminder targeting a colleague; the sweep fires it into the target's intray on the due date; cancel
// removes a pending reminder; a ServiceAccount can't set one; a target who can't see the document is rejected.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentRemindersTests
{
    private readonly E2EApiFactory _factory;

    public DocumentRemindersTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Set_a_reminder_for_a_colleague_it_fires_on_the_sweep_and_can_be_cancelled()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "remind-1234";
        // Setter (A) and target (B) can both see everything (tenant admins); C cannot see the document.
        var aEmail = $"rem-a-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, aEmail, password, "Setter A");
        await _factory.GrantTenantAdminAsync(aEmail);
        using var setter = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(aEmail, password));

        var bEmail = $"rem-b-{Guid.NewGuid():N}@e2e.local";
        var bId = await _factory.SeedUserAsync(tenantId, bEmail, password, "Target B");
        await _factory.GrantTenantAdminAsync(bEmail);
        using var target = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(bEmail, password));

        var cEmail = $"rem-c-{Guid.NewGuid():N}@e2e.local";
        var cId = await _factory.SeedUserAsync(tenantId, cEmail, password, "Outsider C");

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Rem {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "rem-doc" })).GetProperty("id").GetGuid();

        // A ServiceAccount can't set a reminder (no in-app intray).
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 0 })).StatusCode);

        // Targeting a user who can't see the document is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await setter.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 0, targetUserId = cId })).StatusCode);

        // A past due date is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await setter.PostAsJsonAsync($"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddMinutes(-5), recurrence = 0 })).StatusCode);

        // A sets a reminder targeting B, with a note.
        var created = await TestJson.Post(setter, $"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(1), note = "Renewal due", recurrence = 0, targetUserId = bId });
        var reminderId = created.GetProperty("id").GetGuid();
        Assert.Equal(bId, created.GetProperty("targetUserId").GetGuid());

        // Both A (creator) and B (target) see it in the document's reminder list.
        Assert.Contains((await TestJson.Get(setter, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == reminderId);
        Assert.Contains((await TestJson.Get(target, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == reminderId);

        // Back-date it and run the sweep → B gets a DocumentReminder notification carrying the note.
        await _factory.BackdateReminderAsync(reminderId);
        Assert.True(await _factory.RunReminderSweepAsync() >= 1);
        var bIntray = (await TestJson.Get(target, "/api/notifications")).GetProperty("notifications").EnumerateArray().ToList();
        var fired = bIntray.Single(n => n.GetProperty("type").GetString() == "DocumentReminder" && n.GetProperty("documentId").GetGuid() == docId);
        Assert.Contains("Renewal due", fired.GetProperty("body").GetString());

        // The one-shot is done — it no longer appears as pending.
        Assert.DoesNotContain((await TestJson.Get(target, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == reminderId);

        // Cancel path: A sets another reminder for itself, then cancels it.
        var second = await TestJson.Post(setter, $"/api/documents/{docId}/reminders", new { remindAt = DateTimeOffset.UtcNow.AddDays(2), recurrence = 2 });
        var secondId = second.GetProperty("id").GetGuid();
        (await setter.DeleteAsync($"/api/documents/{docId}/reminders/{secondId}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await TestJson.Get(setter, $"/api/documents/{docId}/reminders")).GetProperty("reminders").EnumerateArray(), r => r.GetProperty("id").GetGuid() == secondId);
    }

    // #1911: the zone a reminder was set in rides on the create, is validated, is handed back, and keeps a
    // recurring reminder at its wall time when the sweep advances it on Postgres (timestamptz answers in UTC).
    [Fact]
    public async Task A_reminder_carries_the_zone_it_was_set_in_and_repeats_at_that_wall_time()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "remind-1234";
        var email = $"rem-tz-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, password, "Zoned Setter");
        await _factory.GrantTenantAdminAsync(email);
        using var user = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"RemTz {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = "rem-tz-doc" })).GetProperty("id").GetGuid();
        var href = $"/api/documents/{docId}/reminders";

        // An unknown zone is refused by name, not stored and not ignored.
        var refused = await user.PostAsJsonAsync(href, new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 1, timeZone = "Mars/Olympus_Mons" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("UNKNOWN_REMINDER_TIME_ZONE", await refused.Content.ReadAsStringAsync());

        // Omitted (an old client) is accepted and has none.
        var unzoned = await TestJson.Post(user, href, new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 1 });
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unzoned.GetProperty("timeZone").ValueKind);

        // Sent, it is stored and handed back.
        var zoned = await TestJson.Post(user, href, new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence = 1, timeZone = "Europe/Zurich" });
        var zonedId = zoned.GetProperty("id").GetGuid();
        Assert.Equal("Europe/Zurich", zoned.GetProperty("timeZone").GetString());

        // Due at 09:00 Zurich on the OTHER side of a clock change from the next occurrence, so the sweep's
        // catch-up crosses one: a summer start unless the next landing is in summer too, then a winter one.
        var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        var now = DateTimeOffset.UtcNow;
        var summer = new DateTimeOffset(now.Year - 1, 7, 15, 9, 0, 0, TimeSpan.FromHours(2));
        var landing = new DateTimeOffset(now.UtcDateTime.Date + summer.UtcDateTime.TimeOfDay, TimeSpan.Zero);
        landing = landing > now ? landing : landing.AddDays(1);
        var start = zurich.IsDaylightSavingTime(landing)
            ? new DateTimeOffset(now.Year - 1, 1, 15, 9, 0, 0, TimeSpan.FromHours(1))
            : summer;
        await _factory.BackdateReminderAsync(zonedId, start);
        Assert.True(await _factory.RunReminderSweepAsync() >= 1);

        var advanced = (await TestJson.Get(user, href)).GetProperty("reminders").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == zonedId);
        var next = advanced.GetProperty("remindAt").GetDateTimeOffset();
        Assert.True(next > now);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(next, zurich).DateTime));
        Assert.Equal("Europe/Zurich", advanced.GetProperty("timeZone").GetString());
    }
}
