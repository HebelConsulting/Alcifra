using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Notifications;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Infrastructure.Reminders;

namespace Alcifra.IntegrationTests;

// Verifies the document-reminder sweep (ADR "Document reminders"): a due one-shot fires (notifies the target,
// stamps FiredAt); a due recurring one fires and advances RemindAt to the next future occurrence without
// stamping; a not-yet-due reminder is untouched.
public class DocumentReminderServiceTests
{
    private static AlcifraDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AlcifraDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    [Fact]
    public async Task Fires_due_reminders_advances_recurring_and_leaves_future_ones()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var target = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "t@acme.test", DisplayName = "Target", CreatedAt = DateTimeOffset.UtcNow };
        var doc = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Contract", CreatedByUserId = target.Id, CreatedAt = DateTimeOffset.UtcNow };

        var now = DateTimeOffset.UtcNow;
        var oneShot = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = now.AddMinutes(-5), Recurrence = ReminderRecurrence.None, CreatedByUserId = target.Id, CreatedAt = now.AddDays(-1) };
        var recurring = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = now.AddDays(-10), Recurrence = ReminderRecurrence.Weekly, CreatedByUserId = target.Id, CreatedAt = now.AddDays(-20) };
        var future = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = now.AddDays(3), Recurrence = ReminderRecurrence.None, CreatedByUserId = target.Id, CreatedAt = now };

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(target);
            seed.Documents.Add(doc);
            seed.DocumentReminders.AddRange(oneShot, recurring, future);
            await seed.SaveChangesAsync();
        }

        using (var act = CreateContext(connection))
        {
            var fired = await new DocumentReminderService(act, NullLogger<DocumentReminderService>.Instance).SweepAsync();
            Assert.Equal(2, fired); // the one-shot + the recurring, not the future one
        }

        using var read = CreateContext(connection);
        // Two notifications for the target, both DocumentReminder.
        var notifications = await read.Notifications.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n => Assert.Equal(NotificationType.DocumentReminder, n.Type));
        Assert.All(notifications, n => Assert.Equal(target.Id, n.RecipientUserId));

        var reminders = await read.DocumentReminders.IgnoreQueryFilters().ToDictionaryAsync(r => r.Id);
        // One-shot: FiredAt stamped (done).
        Assert.NotNull(reminders[oneShot.Id].FiredAt);
        // Recurring: still pending, RemindAt advanced to the next future weekly occurrence (within 7 days, on
        // the same weekday as the original).
        Assert.Null(reminders[recurring.Id].FiredAt);
        Assert.True(reminders[recurring.Id].RemindAt > now);
        Assert.True(reminders[recurring.Id].RemindAt <= now.AddDays(7));
        Assert.Equal(now.AddDays(-10).DayOfWeek, reminders[recurring.Id].RemindAt.DayOfWeek);
        // Future: untouched.
        Assert.Null(reminders[future.Id].FiredAt);
        Assert.Equal(future.RemindAt, reminders[future.Id].RemindAt);
    }

    // #1911: a recurring reminder set in a zone repeats at the same wall time there, across clock changes, while
    // one without a zone keeps the UTC arithmetic. The start is chosen on the OTHER side of a change from today,
    // so the catch-up crosses at least one, whichever day this runs on.
    [Fact]
    public async Task A_zoned_recurring_reminder_keeps_its_wall_time_and_an_unzoned_one_keeps_utc()
    {
        var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        var now = DateTimeOffset.UtcNow;
        // A summer start (07:00Z) unless an unzoned reminder starting there would next land in summer too, where
        // its UTC arithmetic happens to be right; then a winter one (08:00Z). Judged at the LANDING, not at now:
        // the day before a change, the two differ.
        var summer = new DateTimeOffset(now.Year - 1, 7, 15, 9, 0, 0, TimeSpan.FromHours(2));
        var landing = new DateTimeOffset(now.UtcDateTime.Date + summer.UtcDateTime.TimeOfDay, TimeSpan.Zero);
        landing = landing > now ? landing : landing.AddDays(1);
        var start = zurich.IsDaylightSavingTime(landing)
            ? new DateTimeOffset(now.Year - 1, 1, 15, 9, 0, 0, TimeSpan.FromHours(1))
            : summer;

        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = now };
        var target = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "t@acme.test", DisplayName = "Target", CreatedAt = now };
        var doc = new Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Contract", CreatedByUserId = target.Id, CreatedAt = now };
        var zoned = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = start, Recurrence = ReminderRecurrence.Daily, TimeZoneId = "Europe/Zurich", CreatedByUserId = target.Id, CreatedAt = start };
        var unzoned = new DocumentReminder { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = target.Id, DocumentId = doc.Id, RemindAt = start, Recurrence = ReminderRecurrence.Daily, CreatedByUserId = target.Id, CreatedAt = start };

        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(target);
            seed.Documents.Add(doc);
            seed.DocumentReminders.AddRange(zoned, unzoned);
            await seed.SaveChangesAsync();
        }

        using (var act = CreateContext(connection))
        {
            Assert.Equal(2, await new DocumentReminderService(act, NullLogger<DocumentReminderService>.Instance).SweepAsync());
        }

        using var read = CreateContext(connection);
        var reminders = await read.DocumentReminders.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(r => r.Id);

        Assert.Equal("Europe/Zurich", reminders[zoned.Id].TimeZoneId);
        Assert.True(reminders[zoned.Id].RemindAt > now);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(reminders[zoned.Id].RemindAt, zurich).DateTime));

        // The old behaviour, kept for a reminder without a zone: the same UTC time of day as the start, which on
        // the other side of the change is 08:00 or 10:00 on the wall.
        Assert.Null(reminders[unzoned.Id].TimeZoneId);
        Assert.Equal(start.UtcDateTime.TimeOfDay, reminders[unzoned.Id].RemindAt.UtcDateTime.TimeOfDay);
        Assert.NotEqual(new TimeOnly(9, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(reminders[unzoned.Id].RemindAt, zurich).DateTime));
    }
}
