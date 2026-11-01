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

        if (await new DeviceEditDialog(row.Device, list.EncryptMailLocked, request => _view.ChangeAsync(row.Device, request))
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

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
