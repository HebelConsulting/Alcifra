using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// A new device's hand-over (#1970 slices 2 and 3): its password, shown this once, a QR code of the password alone, and,
/// where the device reaches mail, calendars or contacts, the QR code of the one-time profile link for a phone's camera;
/// then every server address with its own Copy (#461).
/// </summary>
public partial class DeviceHandOverDialog : Window
{
    public DeviceHandOverDialog() : this(null)
    {
    }

    public DeviceHandOverDialog(EnrolledDevice? enrolled)
    {
        InitializeComponent();
        if (enrolled is null)
        {
            return;
        }

        Title = string.Format(CultureInfo.CurrentCulture, Strings.Get("DvHandOverTitle"), enrolled.Device.Name);
        PasswordBox.Text = enrolled.Password;
        PasswordQr.Source = BitmapOf(enrolled.HandOver.PasswordQr);
        if (enrolled.HandOver.ProfileQr is { } profileQr && enrolled.HandOver.ProfileExpiresAt is { } expires)
        {
            ProfileQr.Source = BitmapOf(profileQr);
            ProfileExpires.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("DvProfileExpires"),
                expires.InZone(SessionTimeZone.Current).ToString("t", CultureInfo.CurrentCulture));
        }
        else
        {
            ProfilePanel.IsVisible = false;
        }

        ServerLines.ItemsSource = DevicesView.ServerLinesOf(enrolled.HandOver.Servers);
    }

    // The server draws the codes (QrCodes, ADR 0913) and sends PNG data URLs, so no QR library is needed here.
    private static Bitmap? BitmapOf(string dataUrl) =>
        dataUrl.IndexOf("base64,", StringComparison.Ordinal) is var at and >= 0
            ? new Bitmap(new MemoryStream(Convert.FromBase64String(dataUrl[(at + "base64,".Length)..])))
            : null;

    private void OnCopyPassword(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync(PasswordBox.Text));

    private void OnCopyLine(object? sender, RoutedEventArgs e) => Safe.Fire(() => CopyAsync((sender as Control)?.Tag as string));

    private async Task CopyAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(value);
        StatusText.Text = Strings.Get("DvCopied");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
