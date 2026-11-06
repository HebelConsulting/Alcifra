using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.Views;

// The Tube tab's view (#2148), its own UserControl like the Contacts tab.
public partial class TubeTab : UserControl
{
    public TubeTab() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Add a video to the channel whose row carries the button (ADR 0559). The picker lives here, in the view; the bytes
    // are read here too, so the view-model files plain names and bytes and stays testable without a display.
    private void OnAdd(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not TubeTabViewModel tab
            || sender is not Button { Tag: TubeChannelRow channel }
            || TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage })
        {
            return;
        }

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = string.Format(Strings.Get("TubeAddTitle"), channel.Name),
            AllowMultiple = true,
        });

        var files = new List<(string Name, byte[] Bytes)>();
        foreach (var file in picked)
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            files.Add((file.Name, buffer.ToArray()));
        }

        await tab.AddAsync(channel, files);
    });
}
