using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The CI/CD secret editor's window (ADR 1084). The view's share is what only a window can do: pick a file, take a dropped
/// one, and close once the view model says the entry was saved. A variable's value is typed in the clear; a secret's is
/// masked while typed.
/// </summary>
public partial class CiCdEntryDialog : Window
{
    // Big enough for any signing certificate or key; a CI secret itself holds at most 48 KiB on the platform.
    private const long MaxFileBytes = 1024 * 1024;

    // Parameterless ctor for the XAML designer/loader only.
    public CiCdEntryDialog() : this(new CiCdEntryEditorViewModel(null, (_, _) => Task.CompletedTask))
    {
    }

    public CiCdEntryDialog(CiCdEntryEditorViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        viewModel.Saved = () => Close(true);
        if (this.FindControl<DockPanel>("DropArea") is { } area)
        {
            area.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            area.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    private CiCdEntryEditorViewModel Editor => (CiCdEntryEditorViewModel)DataContext!;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnRemoveKey(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CiCdKeyRowViewModel row)
        {
            Editor.RemoveKeyCommand.Execute(row);
        }
    }

    private void OnChooseFile(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if ((sender as Control)?.DataContext is not CiCdKeyRowViewModel row)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.Get("CiCdChooseFile"), AllowMultiple = false });
        if (files.Count == 1 && await ReadAsync(files[0]) is { } bytes)
        {
            row.UseFile(files[0].Name, bytes);
        }
    });

#pragma warning disable CS0618 // DragEventArgs.Data / DataFormats.Files — the pre-DataTransfer drag API, as WorkbenchDragDrop uses
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = DroppedFiles(e) is { Count: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    // Each file dropped becomes a new file key, named after it; the person may rename it before saving.
    private void OnDrop(object? sender, DragEventArgs e) => Safe.Fire(async () =>
    {
        foreach (var file in DroppedFiles(e) ?? [])
        {
            if (await ReadAsync(file) is { } bytes)
            {
                Editor.AddFile(file.Name, bytes);
            }
        }
    });

    private static IReadOnlyList<IStorageFile>? DroppedFiles(DragEventArgs e) =>
        e.Data.GetFiles()?.ToList() is { Count: > 0 } items && items.All(i => i is IStorageFile) ? [.. items.Cast<IStorageFile>()] : null;
#pragma warning restore CS0618

    // A file larger than any secret could be is refused with the reason rather than read whole into memory.
    private async Task<byte[]?> ReadAsync(IStorageFile file)
    {
        if ((await file.GetBasicPropertiesAsync()).Size is > MaxFileBytes)
        {
            Editor.Problems = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Get("CiCdFileTooLarge"), file.Name);
            return null;
        }

        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
