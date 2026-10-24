using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Remind… dialog (ADR "Document reminders"): set / list / cancel reminders on a document.
// Interactive — does its own API calls via the SimplArchiveApiClient it's constructed with.
public partial class ReminderDialogViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;
    private readonly string _remindersHref;

    public ReminderDialogViewModel(SimplArchiveApiClient api, string remindersHref, string documentName)
    {
        _api = api;
        _remindersHref = remindersHref;
        DocumentName = documentName;
    }

    public string DocumentName { get; }

    // The shared labels (Presentation.ReminderRepeat), indexed by the value the server stores; the phone lists the same.
    public string[] RecurrenceOptions { get; } = [.. SimplArchive.Presentation.ReminderRepeat.LabelKeys.Select(Strings.Get)];

    // Tomorrow as a calendar day in the session's zone, the zone the typed time is read in below (#1909,
    // Presentation.ReminderSchedule) — the same rule the web and the phone preset.
    [ObservableProperty] private DateTime? _reminderDate = ReminderSchedule.DefaultDate(TimeProvider.System.GetUtcNow(), SessionTimeZone.Current).ToDateTime(TimeOnly.MinValue);
    // Typed, not spun (#1057): the reminder's time is something the user already knows. Seeded from the
    // 09:00 default the spinner used to show.
    [ObservableProperty] private string _reminderTimeEntry = ReminderSchedule.DefaultTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private int _recurrenceIndex;
    [ObservableProperty] private UserOptionInfo? _selectedTarget;
    [ObservableProperty] private string _status = string.Empty;

    // The target picker: "Myself" (Id = Empty) first, then the tenant's active users.
    public ObservableCollection<UserOptionInfo> Targets { get; } = [];
    public ObservableCollection<RemindersClient.ReminderInfo> Reminders { get; } = [];

    // The reminders collection is read FIRST because it carries both halves of this dialog: the rows, and the
    // address of the target picker. Loading the picker separately would read the document and the collection
    // twice over (ADR 0543, issue #416) — following rels is not supposed to cost a request per rel.
    public async Task LoadAsync()
    {
        Targets.Clear();
        Targets.Add(new UserOptionInfo(Guid.Empty, Strings.Get("RemMyself")));
        SelectedTarget = Targets[0];

        string? targetsHref = null;
        Reminders.Clear();
        try
        {
            var (reminders, href) = await _api.Documents.GetRemindersViewAsync(_remindersHref);
            targetsHref = href;
            foreach (var r in reminders)
            {
                Reminders.Add(r);
            }
        }
        catch (Exception)
        {
            // best-effort
        }

        if (targetsHref is null)
        {
            return;
        }

        try
        {
            foreach (var t in await _api.Reminders.GetReminderTargetsAsync(targetsHref))
            {
                Targets.Add(t);
            }
        }
        catch (Exception)
        {
            // best-effort — the picker then offers "Myself" only.
        }
    }

    // Only the rows change after a create/cancel; the picker and its address do not, so a reload re-reads the
    // collection alone rather than repeating the whole load.
    private async Task ReloadRemindersAsync()
    {
        Reminders.Clear();
        try
        {
            foreach (var r in await _api.Documents.GetRemindersAsync(_remindersHref))
            {
                Reminders.Add(r);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (ReminderDate is not { } date)
        {
            return;
        }

        try
        {
            if (!SimplArchive.Presentation.DocumentDateFormat.TryParseTypedTime(ReminderTimeEntry, out var typed))
            {
                Status = Strings.Get("TimeEntryInvalid");
                return;
            }

            // In the session's zone (the user's preference, else the machine's), as the default above was made.
            var when = WallClock.At(DateOnly.FromDateTime(date), typed ?? TimeOnly.MinValue, SessionTimeZone.Current);
            var targetId = SelectedTarget is { } t && t.Id != Guid.Empty ? t.Id : (Guid?)null;
            await _api.Documents.CreateReminderAsync(_remindersHref, when, string.IsNullOrWhiteSpace(Note) ? null : Note, RecurrenceIndex, targetId);
            Note = string.Empty;
            Status = Strings.Get("StReminderSet");
            await ReloadRemindersAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task CancelReminderAsync(RemindersClient.ReminderInfo reminder)
    {
        try
        {
            await _api.Reminders.CancelReminderAsync(reminder);
            await ReloadRemindersAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }
}
