using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

/// <summary>One index field's editor row, typed to its <c>MaskFieldEditViewModel</c> (see the markup).</summary>
public partial class MaskFieldEditorRow : UserControl
{
    public MaskFieldEditorRow() => AvaloniaXamlLoader.Load(this);

    // "From a file" (#2214): the file dialog is the view's, the filing is the field's (ADR 0730).
    private void OnFromFile(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MaskFieldEditViewModel field || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("RefPickerFromFile"),
            AllowMultiple = false,
        });
        if (files.Count != 1)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        await field.FileAsync(files[0].Name, buffer.ToArray());
    });
}
