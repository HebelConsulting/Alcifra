using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The Messages tab's view (core #1813). Its own UserControl, as every tab since #519. The save and open dialogs are
// the view's to give (ADR 0730): the view model asks for a stream and never sees a window.
public partial class MessagesTab : UserControl
{
    public MessagesTab()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MessagesTabViewModel tab)
            {
                tab.SaveFile = SaveFileAsync;
                tab.OpenFile = OpenFileAsync;
                tab.Bubbles.CollectionChanged += (_, _) => this.FindControl<ScrollViewer>("Thread")?.ScrollToEnd();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task<Stream?> SaveFileAsync(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Get("MsgBackUpToFile"),
            SuggestedFileName = suggestedName,
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    private async Task<Stream?> OpenFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.Get("MobBackupRestore"), AllowMultiple = false });
        return files.Count == 0 ? null : await files[0].OpenReadAsync();
    }
}
