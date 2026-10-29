using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

// The Passwords tab's view (ADR 0968). Its own UserControl, as every tab since #519. The dialogs — confirm, a name, a
// folder of the store, the export to import and its conflicts, deleting the export, enrolling a spare key — are the
// view's to give (ADR 0730): the view model asks and never sees a window. Esc cancels the form, Cmd/Ctrl+S saves it
// (ADR 0550), and the window leaving the front hides every secret held open.
public partial class PasswordsTab : UserControl
{
    private PasswordsTabViewModel? _tab;
    private Window? _window;

    public PasswordsTab()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind(DataContext as PasswordsTabViewModel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        if (this.FindControl<ListBox>("ItemList") is { } list)
        {
            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedItem is PasswordItemRowViewModel row)
                {
                    _tab?.Enter(row);
                }
            };
        }
    }

    private void Bind(PasswordsTabViewModel? tab)
    {
        _tab = tab;
        if (tab is null)
        {
            return;
        }

        var dialogs = tab.Dialogs;
        dialogs.Confirm = ConfirmAsync;
        dialogs.Inform = InformAsync;
        dialogs.AskName = AskNameAsync;
        dialogs.PickFolder = PickFolderAsync;
        dialogs.PickExport = PickExportAsync;
        dialogs.AskConflict = AskConflictAsync;
        dialogs.DeleteFile = DeleteFileAsync;
        dialogs.EnrolKey = EnrolKeyAsync;
        dialogs.EditAudience = EditAudienceAsync;
    }

    private async Task<IReadOnlyList<Alcifra.ApiClient.AudienceChange>?> EditAudienceAsync(Alcifra.ApiClient.ContentAudienceInfo audience, string subject) =>
        Owner is { } owner
            ? await new ContentAudienceDialog(audience, subject).ShowDialog<IReadOnlyList<Alcifra.ApiClient.AudienceChange>?>(owner)
            : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        e.Handled = e.Key switch
        {
            Key.Escape => _tab.Escape(),
            Key.S when command => _tab.Save(),
            _ => false,
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _window = window;
            window.Deactivated += OnDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.Deactivated -= OnDeactivated;
            _window = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnDeactivated(object? sender, EventArgs e) => _tab?.WindowDeactivated();

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async Task<bool> ConfirmAsync(string title, string message, string confirm) =>
        Owner is { } owner && await new ConfirmDialog(message, confirm) { Title = title }.ShowDialog<bool>(owner);

    private async Task InformAsync(string title, string message)
    {
        if (Owner is { } owner)
        {
            await ConfirmDialog.Notice(title, message, Strings.Get("Ok")).ShowDialog<bool>(owner);
        }
    }

    // A rename starts from the current name; a new folder from nothing.
    private async Task<string?> AskNameAsync(string title, string label, string initial)
    {
        if (Owner is not { } owner)
        {
            return null;
        }

        return initial.Length > 0
            ? await new RenameDialog(initial) { Title = title }.ShowDialog<string?>(owner)
            : await new NewFolderDialog(title, label).ShowDialog<string?>(owner);
    }

    private async Task<PasswordFolderInfo?> PickFolderAsync(IReadOnlyList<PasswordFolderChoice> choices) =>
        Owner is { } owner ? await new PasswordFolderPickerDialog(choices).ShowDialog<PasswordFolderInfo?>(owner) : null;

    private async Task<PasswordImportResolution?> AskConflictAsync(PasswordImportConflict conflict) =>
        Owner is { } owner ? await new PasswordConflictDialog(conflict).ShowDialog<PasswordImportResolution?>(owner) : null;

    // The export read whole on this machine; its path kept so the plaintext file can be deleted afterwards.
    private async Task<PickedPasswordExport?> PickExportAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("PwImportGoogle"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return new PickedPasswordExport(files[0].Name, await reader.ReadToEndAsync(), files[0].TryGetLocalPath());
    }

    private static Task<bool> DeleteFileAsync(string path)
    {
        try
        {
            File.Delete(path);
            return Task.FromResult(!File.Exists(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Services.DesktopLog.Debug("The imported export could not be deleted: {Reason}", e.Message);
            return Task.FromResult(false);
        }
    }

    private async Task EnrolKeyAsync()
    {
        if (Owner is { } owner && _tab?.CurrentApi is { } api)
        {
            await new SelfEnrolmentDialog(api).ShowDialog(owner);
        }
    }
}
