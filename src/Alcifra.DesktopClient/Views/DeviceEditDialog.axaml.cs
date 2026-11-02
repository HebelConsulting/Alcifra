using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// Enrols a device, or changes an enrolled one (#1970 slice 3): its name, what it reaches, and whether its mail is
/// sealed. The act itself is the caller's, so a refusal (a name already taken, no hardware key) is shown here and the
/// form stays open to correct it.
/// </summary>
public partial class DeviceEditDialog : Window
{
    private readonly Func<DeviceRequest, Task> _save;
    private readonly ProtocolDevice? _device;
    private readonly List<(Guid Id, CheckBox Box)> _ticks = [];
    private bool _collectionsLoaded;

    public DeviceEditDialog() : this(null, encryptLocked: false, _ => Task.CompletedTask)
    {
    }

    /// <param name="device">The device to change, or null to enrol a new one.</param>
    /// <param name="encryptLocked">The organisation seals mail: the setting is shown on and cannot be changed.</param>
    /// <param name="save">Enrols or changes; throws with the user's message when refused.</param>
    /// <param name="collections">The shared calendars and address books the device can tick (#2090); null when enrolling.</param>
    public DeviceEditDialog(ProtocolDevice? device, bool encryptLocked, Func<DeviceRequest, Task> save,
        Func<Task<IReadOnlyList<DeviceCollectionChoice>>>? collections = null)
    {
        InitializeComponent();
        _save = save;
        _device = device;
        if (device is not null)
        {
            Title = Strings.Get("DvChange");
            NameBox.Text = device.Name;
            MailBox.IsChecked = device.Mail;
            FilesBox.IsChecked = device.Files;
            CalendarsBox.IsChecked = device.Calendars;
            ContactsBox.IsChecked = device.Contacts;
            EncryptBox.IsChecked = device.EncryptMail;
        }

        if (device is { OffersCollections: true } && collections is not null)
        {
            CollectionsPanel.IsVisible = true;
            ChosenCollections.IsChecked = device.CollectionsChosen;
            AllCollections.IsChecked = !device.CollectionsChosen;
            Safe.Fire(async () => ShowCollections(await collections()));
        }

        if (encryptLocked)
        {
            EncryptBox.IsChecked = true;
            EncryptBox.IsEnabled = false;
            LockedText.IsVisible = true;
        }
    }

    /// <summary>
    /// The request the form states now. The collections are stated only once their choices were read: until then the
    /// stored setting and ticks are sent back as they were (a null would leave them too, but a full PUT says so).
    /// </summary>
    public DeviceRequest Request => new(
        NameBox.Text?.Trim() ?? string.Empty,
        MailBox.IsChecked == true, FilesBox.IsChecked == true, CalendarsBox.IsChecked == true, ContactsBox.IsChecked == true,
        EncryptBox.IsEnabled && EncryptBox.IsChecked == true,
        _collectionsLoaded ? ChosenCollections.IsChecked == true : _device?.CollectionsChosen,
        _collectionsLoaded ? [.. _ticks.Where(t => t.Box.IsChecked == true).Select(t => t.Id)] : _device?.ChosenCollections);

    // The shared collections as ticks, calendars first (the server's order); "nothing shared" where there are none.
    private void ShowCollections(IReadOnlyList<DeviceCollectionChoice> choices)
    {
        CollectionList.Children.Clear();
        _ticks.Clear();
        foreach (var choice in choices)
        {
            var box = new CheckBox
            {
                Content = $"{Strings.Get(choice.IsCalendar ? "DvCalendars" : "DvContacts")}: {choice.Name}",
                IsChecked = choice.Ticked,
            };
            _ticks.Add((choice.Id, box));
            CollectionList.Children.Add(box);
        }

        if (choices.Count == 0)
        {
            CollectionList.Children.Add(new TextBlock { Text = Strings.Get("DvCollectionsNone"), FontSize = 12 });
        }

        _collectionsLoaded = true;
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        SaveButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        try
        {
            await _save(Request);
            Close(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    });

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
