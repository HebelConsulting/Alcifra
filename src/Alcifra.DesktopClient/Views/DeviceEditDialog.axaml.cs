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

    public DeviceEditDialog() : this(null, encryptLocked: false, _ => Task.CompletedTask)
    {
    }

    /// <param name="device">The device to change, or null to enrol a new one.</param>
    /// <param name="encryptLocked">The organisation seals mail: the setting is shown on and cannot be changed.</param>
    /// <param name="save">Enrols or changes; throws with the user's message when refused.</param>
    public DeviceEditDialog(ProtocolDevice? device, bool encryptLocked, Func<DeviceRequest, Task> save)
    {
        InitializeComponent();
        _save = save;
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

        if (encryptLocked)
        {
            EncryptBox.IsChecked = true;
            EncryptBox.IsEnabled = false;
            LockedText.IsVisible = true;
        }
    }

    /// <summary>The request the form states now.</summary>
    public DeviceRequest Request => new(
        NameBox.Text?.Trim() ?? string.Empty,
        MailBox.IsChecked == true, FilesBox.IsChecked == true, CalendarsBox.IsChecked == true, ContactsBox.IsChecked == true,
        EncryptBox.IsEnabled && EncryptBox.IsChecked == true);

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
