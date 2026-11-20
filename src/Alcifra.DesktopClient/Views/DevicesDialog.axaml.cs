using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The user's devices (#1970 slice 3), replacing the WebDAV and IMAP access dialogs: each device's own password, what it
/// reaches, whether its mail is sealed; enrol, change and revoke; the mail view; every server address. Draws the shared
/// <see cref="DevicesView"/> and decides nothing of its own.
/// </summary>
public partial class DevicesDialog : Window
{
    private readonly DevicesView? _view;
    private bool _loading;

    public DevicesDialog() : this(null)
    {
    }

    public DevicesDialog(AlcifraApiClient? api)
    {
        InitializeComponent();
        if (api is null)
        {
            return;
        }

        _view = new DevicesView(api.Devices);
        Opened += (_, _) => Safe.Fire(ReloadAsync);
    }

    private async Task ReloadAsync()
    {
        if (_view is null)
        {
            return;
        }

        try
        {
            await _view.LoadAsync();
            Draw();
        }
        catch (Exception e)
        {
            StatusText.Text = e.Message;
        }
    }

    private void Draw()
    {
        if (_view?.List is not { } list)
        {
            return;
        }

        _loading = true;
        ShowAllBox.IsChecked = list.ShowAllDocuments;
        _loading = false;
        DeviceRows.ItemsSource = _view.Rows;
        EarlierNote.IsVisible = list.Items.Any(d => d.IsEarlier);
        ServerLines.ItemsSource = _view.ServerLines;
    }

    private void OnShowAllChanged(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_loading || _view is null)
        {
            return;
        }

        try
        {
            await _view.SetShowAllDocumentsAsync(ShowAllBox.IsChecked == true);
            Draw();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    });

    private void OnEnrol(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_view?.List is not { } list)
        {
            return;
        }

        var form = new DeviceEditDialog(null, list.EncryptMailLocked, request => _view.EnrolAsync(request));
        if (await form.ShowDialog<bool>(this) && _view.HandOver is { } enrolled)
        {
            Draw();
            await new DeviceHandOverDialog(enrolled).ShowDialog(this);
            _view.CloseHandOver();
        }
    });

    private void OnChange(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_view?.List is not { } list || (sender as Control)?.Tag is not DeviceRow row)
        {
            return;
        }

        if (await new DeviceEditDialog(row.Device, list.EncryptMailLocked, request => _view.ChangeAsync(row.Device, request),
                () => _view.CollectionsAsync(row.Device))
                .ShowDialog<bool>(this))
        {
            Draw();
        }
    });

    private void OnRevoke(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_view is null || (sender as Control)?.Tag is not DeviceRow row)
        {
            return;
        }

        var message = string.Format(CultureInfo.CurrentCulture, Strings.Get("DvRevokeConfirm"), row.Name);
        if (!await new ConfirmDialog(message, Strings.Get("DvRevoke")).ShowDialog<bool>(this))
        {
            return;
        }

        try
        {
            await _view.RevokeAsync(row.Device);
            Draw();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    });

    private void OnCopyLine(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if ((sender as Control)?.Tag is string { Length: > 0 } value && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(value);
            StatusText.Text = Strings.Get("DvCopied");
        }
    });

    // Connect WebDAV for ONE device (#2394, owner-decided): every device mounts the SAME address and user name — only
    // the password differs — so the act is "mount with this device's password": the saved system password (which
    // belongs to whichever device used it last) is forgotten first, the system asks, and the person pastes this
    // device's. The address and user name are shown beside it with Copy, because the prompt asks for both.
    private void OnConnect(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (_view?.List is not { } list || (sender as Control)?.Tag is not DeviceRow row || !row.Device.Files)
        {
            return;
        }

        ConnectHeading.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("DvConnectHeading"), row.Name);
        ConnectUser.Text = list.Servers.Username;
        ConnectAddress.Text = list.Servers.WebDavUrl;
        ConnectPanel.IsVisible = true;

        var address = list.Servers.WebDavUrl.TrimEnd('/');
        var lastDevice = LayoutSettingsStore.Load().LastWebDavDevice;

        if (OsFileManager.MountedPathFor(address) is { } mounted)
        {
            // A volume that no longer answers is no one's to protect — unmount it and mount fresh (the one-click
            // flow's rule). A healthy one is another question: it carries SOME device's password, and the only
            // honest answer to "whose" is the one this desktop last connected with.
            if (await OsFileManager.MountAnswersAsync(mounted, TimeSpan.FromSeconds(5)))
            {
                if (string.Equals(lastDevice, row.Name, StringComparison.Ordinal))
                {
                    var opened = await OsFileManager.OpenLocalFolderAsync(mounted);
                    StatusText.Text = opened.Success ? Strings.Get("MwWebDavMounted") : string.Format(CultureInfo.CurrentCulture, Strings.Get("MwWebDavOpenFailed"), opened.Error);
                    return;
                }

                var question = lastDevice is { Length: > 0 } known
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Get("DvMountOtherKnown"), known)
                    : Strings.Get("DvMountOtherUnknown");
                if (!await new ConfirmDialog(question, Strings.Get("DvConnect")).ShowDialog<bool>(this))
                {
                    return;
                }
            }

            if (await OsFileManager.ForceUnmountAsync(mounted) is { Success: false } notUnmounted)
            {
                StatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("MwWebDavDeadMount"), mounted, notUnmounted.Error);
                return;
            }
        }

        await MountWithAsync(list, row, address);
    });

    /// <summary>Forgets whatever password the system saved for the address, mounts, and remembers which device that was.</summary>
    private async Task MountWithAsync(DeviceList list, DeviceRow row, string address)
    {
        try
        {
            await OsFileManager.ForgetSavedPasswordAsync(address, list.Servers.Username);
            StatusText.Text = Strings.Get("MwWebDavMounting");
            var result = await OsFileManager.OpenWebDavFolderAsync(
                address, "", TryGetPlatformHandle()?.Handle ?? 0, list.Servers.Username);

            if (result.Success)
            {
                var settings = LayoutSettingsStore.Load();
                settings.LastWebDavDevice = row.Name;
                LayoutSettingsStore.Save(settings);
                StatusText.Text = Strings.Get("MwWebDavMounted");
                return;
            }

            StatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("MwWebDavMountFailed"), result.Error);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("MwWebDavMountFailed"), ex.Message);
        }
    }

    private void OnCopyConnectUser(object? sender, RoutedEventArgs e) => CopyToClipboard(ConnectUser.Text);

    private void OnCopyConnectAddress(object? sender, RoutedEventArgs e) => CopyToClipboard(ConnectAddress.Text);

    private void CopyToClipboard(string? value) => Safe.Fire(async () =>
    {
        if (value is { Length: > 0 } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(value);
            StatusText.Text = Strings.Get("DvCopied");
        }
    });

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Fills the window for the headless screenshot hook (#2394): two enrolled devices (one that reaches files, one
    /// that does not — the Connect button disabled with its reason), an earlier password, every server address, and an
    /// open Connect WebDAV panel — the new column and the panel in one frame.
    /// </summary>
    internal void FillForShot()
    {
        static ProtocolDevice Device(string name, string kind, bool files, bool canEdit = true, bool canDelete = true) => new(
            name, kind, Mail: true, Files: files, Calendars: true, Contacts: true, EncryptMail: false, EncryptMailLocked: false,
            CreatedAt: new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero),
            LastUsedAt: new DateTimeOffset(2026, 6, 1, 7, 30, 0, TimeSpan.Zero), canEdit, canDelete, "\"shot\"", null);

        DeviceRow Row(ProtocolDevice device) => new(
            device, device.Name,
            string.Join(", ", new[] { ("Mail", device.Mail), ("Files", device.Files), ("Calendars", device.Calendars), ("Contacts", device.Contacts) }
                .Where(p => p.Item2).Select(p => Strings.Get($"Dv{p.Item1}"))),
            Strings.Get("DvNotEncrypted"),
            device.LastUsedAt is { } used ? used.ToString("g", CultureInfo.CurrentCulture) : Strings.Get("DvNever"),
            device.CanEdit, device.CanDelete);

        var laptop = Row(Device("Laptop", "enrolled", files: true));
        DeviceRows.ItemsSource = new[]
        {
            laptop,
            Row(Device("iPhone", "enrolled", files: false)),
            Row(Device("Earlier WebDAV password", "earlierDav", files: true)),
        };

        var servers = new DeviceServers(
            Username: "demo", ImapAvailable: true, ImapHost: "demo.alcifra.dev", ImapPort: 143, ImapTlsPort: 993,
            OutgoingHost: "demo.alcifra.dev", WebDavUrl: "https://demo.alcifra.dev/webdav/…",
            CalDavUrl: "https://demo.alcifra.dev/caldav/…", CardDavUrl: "https://demo.alcifra.dev/carddav/…");
        ServerLines.ItemsSource = Alcifra.Presentation.DeviceServerLines.Of(
            servers.Username, servers.ImapAvailable, servers.ImapHost, servers.ImapPort, servers.ImapTlsPort,
            servers.OutgoingHost, servers.WebDavUrl, servers.CalDavUrl, servers.CardDavUrl, Strings.Get);

        ConnectHeading.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("DvConnectHeading"), laptop.Name);
        ConnectUser.Text = servers.Username;
        ConnectAddress.Text = servers.WebDavUrl;
        ConnectPanel.IsVisible = true;
    }
}
