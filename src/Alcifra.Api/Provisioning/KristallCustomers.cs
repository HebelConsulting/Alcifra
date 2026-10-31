using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using static Alcifra.Api.Provisioning.KristallSamples;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Kristall's customers and calendars (#1787): what the events-and-artists example showed, written for this
/// company — the Demo tenant shows them since that example retired (#1788).
/// </summary>
/// <remarks>
/// <para>
/// One shared address book with a card per customer, a folder per customer holding a REFERENCE to its card
/// (a Contact's home must be a typed folder, ADR 0619) beside a colour-coded "Visits" calendar, a company
/// calendar with a multi-day all-day entry, and a department mailbox. The administrator's personal space gets
/// two contacts and two appointments, so the phone's Address book and Calendar tiles open on something.
/// </para>
/// <para>
/// Appointments are relative to the seeding instant, in Lucerne's zone: the nightly reset keeps them in the
/// current month, where every calendar opens.
/// </para>
/// </remarks>
internal static class KristallCustomers
{
    private const string Zone = "Europe/Zurich";

    private sealed record Customer(string Name, string Card, string Colour, (string Summary, int InDays, int Hour, string? Rule)[] Visits);

    private static readonly Customer[] Customers =
    [
        new("Hotel Rigiblick", "ManualContactRigiblick.vcf", "#1e88e5",
            [("Façade walk-through", 3, 10, null), ("Annual review", 17, 14, null)]),
        new("Uhrenmanufaktur Weber", "ManualContactWeber.vcf", "#8e24aa",
            [("Showroom glazing — site visit", 6, 9, null)]),
        new("Café am See", "ManualContactSeecafe.vcf", "#f57c00",
            [("Coffee-machine service", 1, 8, "FREQ=MONTHLY;COUNT=6")]),
    ];

    internal static async Task SeedAsync(KristallSeed seed, string mailDomain)
    {
        var addressbookMask = await FolderMask.CurrentVersionIdAsync(seed.Db, seed.TenantId, WellKnownMaskIds.Addressbook, CancellationToken.None);
        var calendarMask = await FolderMask.CurrentVersionIdAsync(seed.Db, seed.TenantId, WellKnownMaskIds.Calendar, CancellationToken.None);
        var mailboxMask = await FolderMask.CurrentVersionIdAsync(seed.Db, seed.TenantId, WellKnownMaskIds.Mailbox, CancellationToken.None);
        var appointments = seed.Services.GetRequiredService<IAppointmentComposer>();
        var today = TimeZoneInfo.ConvertTime(seed.Now, TimeZoneInfo.FindSystemTimeZoneById(Zone)).Date;

        var customers = await seed.FolderAsync(seed.RepositoryId, "Customers", "customers");
        var contacts = await seed.FolderAsync(customers.Id, "Contacts", "customers/contacts", addressbookMask);

        foreach (var customer in Customers)
        {
            var card = await seed.DocAsync(contacts.Id, customer.Name, customer.Card, DateOnly.FromDateTime(today), masked: false);
            var folder = await seed.FolderAsync(customers.Id, customer.Name, $"customers/{Slug(customer.Name)}");
            await DemoSeedItems.AddReferenceAsync(seed.Db, seed.TenantId, folder.Id, card.Id, seed.AdminId, seed.Now);

            var visits = await seed.FolderAsync(folder.Id, "Visits", $"customers/{Slug(customer.Name)}/visits", calendarMask);
            await DemoSeedItems.SetColourAsync(seed.Db, seed.TenantId, visits, calendarMask, customer.Colour);
            foreach (var (summary, inDays, hour, rule) in customer.Visits)
            {
                var start = today.AddDays(inDays).AddHours(hour);
                await AppointmentAsync(seed, appointments, visits.Id, summary,
                    new Appointment(summary, start, start.AddHours(1), false, Zone, Zone, customer.Name, null, rule),
                    $"manual-visit-{Slug(customer.Name)}-{Slug(summary)}");
            }
        }

        // The company's own calendar: an all-day entry spanning days (iCalendar's exclusive DTEND) and a timed one.
        var company = await seed.FolderAsync(seed.RepositoryId, "Company calendar", "company-calendar", calendarMask);
        await DemoSeedItems.SetColourAsync(seed.Db, seed.TenantId, company, calendarMask, "#00897b");
        var offsite = today.AddDays(10);
        await AppointmentAsync(seed, appointments, company.Id, "Team offsite",
            new Appointment("Team offsite", offsite, offsite.AddDays(2), true, null, null, "Engelberg", null, null),
            "manual-company-offsite");
        var board = today.AddDays(4).AddHours(14);
        await AppointmentAsync(seed, appointments, company.Id, "Board meeting",
            new Appointment("Board meeting", board, board.AddHours(2.5), false, Zone, Zone, "Meeting room Pilatus", null, null),
            "manual-company-board");

        // The department mailbox: mail to info@ lands in the archive (#703).
        await DeclaredMailDomain.EnsureAsync(seed.Db, seed.TenantId, mailDomain, seed.Now);
        var mailbox = await seed.FolderAsync(customers.Id, "Mailbox", "customers/mailbox", mailboxMask);
        await DemoSeedItems.ClaimAsync(seed.Db, seed.TenantId, mailbox.Id, $"info@{mailDomain}");

        await PersonalAsync(seed, appointments, today);
    }

    private static async Task PersonalAsync(KristallSeed seed, IAppointmentComposer appointments, DateTime today)
    {
        var personal = await seed.Services.GetRequiredService<PersonalRepositoryProvisioner>()
            .EnsureAsync(seed.AdminId, seed.TenantId, CancellationToken.None,
                slug => DemoId.For(seed.TenantId, $"personal/admin/{slug}"), createdAt: seed.Now);

        async Task<Guid> ChildAsync(string name) =>
            await seed.Db.Documents.Where(d => d.ParentId == personal.Id && d.Name == name).Select(d => d.Id).SingleAsync();

        var addressbook = await ChildAsync(PersonalFolders.MyAddressbook);
        await seed.DocAsync(addressbook, "Michael Gerber", "ManualPersonalGerber.vcf", DateOnly.FromDateTime(today), masked: false);
        await seed.DocAsync(addressbook, "Nina Roth", "ManualPersonalTreuhand.vcf", DateOnly.FromDateTime(today), masked: false);

        var calendar = await ChildAsync(PersonalFolders.MyCalendar);
        var oneToOne = today.AddDays(2).AddHours(11);
        await AppointmentAsync(seed, appointments, calendar, "1:1 with Lukas",
            new Appointment("1:1 with Lukas", oneToOne, oneToOne.AddMinutes(30), false, Zone, Zone, null, null, "FREQ=WEEKLY;COUNT=8"),
            "manual-personal-one-to-one");
        var accountant = today.AddDays(8).AddHours(16);
        await AppointmentAsync(seed, appointments, calendar, "Year-end close with Nina Roth",
            new Appointment("Year-end close with Nina Roth", accountant, accountant.AddHours(1), false, Zone, Zone, "Roth Treuhand, Luzern", null, null),
            "manual-personal-accountant");
    }

    private static Task<Document?> AppointmentAsync(
        KristallSeed seed, IAppointmentComposer composer, Guid calendarId, string name, Appointment appointment, string uid) =>
        DemoSeedItems.ItemAsync(seed.Db, seed.Storage, seed.TenantId, calendarId, name, seed.AdminId, seed.Now,
            seed.Finalizer, composer.Merge(null, appointment, uid), ".ics", "text/calendar");

    private static string Slug(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
