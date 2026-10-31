using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

// The create and edit form of a password entry (ADR 0968). Its keys (Cmd/Ctrl+S saves, Esc cancels) are the tab's, which
// handles them for the form wherever focus is inside it; leaving the screen lets go of every held eye. The two QR buttons
// beside the verification code (ADR 1013) are the view's: only it reaches the clipboard and the file picker.
public partial class PasswordEditForm : UserControl
{
    public PasswordEditForm()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PasswordEntryEditorViewModel { IsCreating: true })
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("NameBox")?.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        (DataContext as PasswordEntryEditorViewModel)?.HideSecrets();
        base.OnDetachedFromVisualTree(e);
    }

    // A screenshot of the site's QR taken with the system's own tool, then this button: the clipboard's image is read.
    private void OnQrFromClipboard(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not PasswordEntryEditorViewModel editor || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        // The framework's own bitmap first (it knows each platform's image formats); the raw formats a screenshot tool
        // writes as a fallback.
        byte[]? image = null;
        using (var bitmap = await clipboard.TryGetBitmapAsync())
        {
            if (bitmap is not null)
            {
                using var png = new MemoryStream();
                bitmap.Save(png);
                image = png.ToArray();
            }
        }

        foreach (var format in QrCodeImage.ClipboardFormats)
        {
            if (image is not null)
            {
                break;
            }

            image = await clipboard.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(format)) is { Length: > 0 } bytes ? bytes : null;
        }

        editor.AcceptScannedCode(image is null ? null : QrCodeImage.Decode(image));
    });

    private void OnQrFromFile(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not PasswordEntryEditorViewModel editor || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("PwQrFromFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll, FilePickerFileTypes.All],
        });
        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        editor.AcceptScannedCode(QrCodeImage.Decode(buffer.ToArray()));
    });
}
