using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.Views;

// The Messages tab's view (core #1813, ADR 0967). Its own UserControl, as every tab since #519. The dialogs — save,
// open, pick files, confirm, the clipboard — are the view's to give (ADR 0730): the view models ask and never see a
// window. The keys: Enter sends, Shift+Enter is a new line, Ctrl/Cmd+S saves an edit, Esc cancels (ADR 0550).
public partial class MessagesTab : UserControl
{
    private MessagesTabViewModel? _tab;
    private MessengerThreadViewModel? _thread;
    private TopLevel? _topLevel;

    public MessagesTab()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as MessagesTabViewModel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        if (this.FindControl<ScrollViewer>("ThreadScroll") is { } scroll)
        {
            scroll.ScrollChanged += OnThreadScrolled;
        }

        if (this.FindControl<DockPanel>("ThreadArea") is { } area)
        {
            area.AddHandler(DragDrop.DragOverEvent, OnThreadDragOver);
            area.AddHandler(DragDrop.DropEvent, OnThreadDrop);
        }
    }

#pragma warning disable CS0618 // DragEventArgs.Data / DataFormats.Files — the pre-DataTransfer drag API, as WorkbenchDragDrop uses
    // Files dropped on the open conversation become chips, as Attach makes them (#2037): never sent before Send. The drag
    // shows "no" where they would not be taken: no conversation, a message being edited, a send on its way, or anything
    // that is not a file (a folder).
    private void OnThreadDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = DropTarget() is not null && DroppedFiles(e) is { Count: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnThreadDrop(object? sender, DragEventArgs e) => Services.Safe.Fire(async () =>
    {
        if (DropTarget() is { } composer && DroppedFiles(e) is { Count: > 0 } files)
        {
            await composer.AddFilesAsync(() => ReadAsync(files));
        }
    });

    private MessengerComposerViewModel? DropTarget() =>
        _tab?.Thread is { CanWrite: true, Composer: { AcceptsFiles: true } composer } ? composer : null;

    // Only files: a folder among them refuses the whole drop rather than dropping part of it.
    private static IReadOnlyList<IStorageFile>? DroppedFiles(DragEventArgs e) =>
        e.Data.GetFiles()?.ToList() is { Count: > 0 } items && items.All(i => i is IStorageFile) ? [.. items.Cast<IStorageFile>()] : null;
#pragma warning restore CS0618

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Bind(MessagesTabViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabChanged;
        }

        _tab = tab;
        if (tab is null)
        {
            return;
        }

        tab.SaveFile = SaveFileAsync;
        tab.OpenFile = OpenFileAsync;
        tab.Dialogs.Confirm = ConfirmAsync;
        tab.Dialogs.Inform = InformAsync;
        tab.Dialogs.PickFiles = PickFilesAsync;
        tab.Dialogs.Copy = CopyAsync;
        tab.PropertyChanged += OnTabChanged;
        Follow(tab.Thread);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessagesTabViewModel.Thread))
        {
            Follow(_tab?.Thread);
        }
    }

    // The thread on show scrolls where its anchor says; the one before is let go of.
    private void Follow(MessengerThreadViewModel? thread)
    {
        if (_thread is not null)
        {
            _thread.ScrollToEndRequested -= ScrollToEnd;
        }

        _thread = thread;
        if (thread is not null)
        {
            thread.ScrollToEndRequested += ScrollToEnd;
            ScrollToEnd();
        }
    }

    // After the rows are laid out, or the end would be the old one.
    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("ThreadScroll")?.ScrollToEnd(), DispatcherPriority.Background);

    private void OnThreadScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_thread is null || sender is not ScrollViewer scroll)
        {
            return;
        }

        if (e.ViewportDelta.Y != 0)
        {
            _thread.Resized();   // the composer grew or the window changed: at the end it stays at the end
            return;
        }

        var atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        _thread.Scrolled(atEnd, byReader: e.OffsetDelta.Y < 0);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tab is null)
        {
            return;
        }

        var composer = _tab.Thread?.Composer;
        var inComposer = e.Source is TextBox { Name: "Compose" };
        var save = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Escape when _tab.Escape():
                e.Handled = true;
                break;
            case Key.Enter when inComposer && composer is not null && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                Services.Safe.Fire(composer.SendAsync);
                break;
            case Key.S when save && composer is { IsEditing: true }:
                e.Handled = true;
                Services.Safe.Fire(composer.SendAsync);
                break;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _topLevel = window;
            window.Activated += OnActivated;
            window.Deactivated += OnDeactivated;
        }

        if (this.FindControl<TextBox>("Compose") is { } compose)
        {
            compose.LostFocus += OnComposeLostFocus;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is Window window)
        {
            window.Activated -= OnActivated;
            window.Deactivated -= OnDeactivated;
        }

        if (this.FindControl<TextBox>("Compose") is { } compose)
        {
            compose.LostFocus -= OnComposeLostFocus;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnActivated(object? sender, EventArgs e) => _tab?.WindowActive(true);

    private void OnDeactivated(object? sender, EventArgs e) => _tab?.WindowActive(false);

    private void OnComposeLostFocus(object? sender, RoutedEventArgs e) => _tab?.Thread?.Composer.LeftField();

    private async Task<bool> ConfirmAsync(string title, string message, string confirm) =>
        TopLevel.GetTopLevel(this) is Window owner && await new ConfirmDialog(message, confirm) { Title = title }.ShowDialog<bool>(owner);

    private async Task InformAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await ConfirmDialog.Notice(title, message, ViewText.Get("OK")).ShowDialog<bool>(owner);
        }
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    // Several files at once, read whole: they are sealed in memory before anything leaves the machine.
    private async Task<IReadOnlyList<PickedFile>> PickFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return [];
        }

        return await ReadAsync(await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = ViewText.Get("Attach"), AllowMultiple = true }));
    }

    // Read into memory, as Attach always did: a file is sealed before anything leaves the machine, and nothing new is
    // written to disk.
    private static async Task<IReadOnlyList<PickedFile>> ReadAsync(IReadOnlyList<IStorageFile> files)
    {
        var picked = new List<PickedFile>();
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            picked.Add(new PickedFile(file.Name, buffer.ToArray()));
        }

        return picked;
    }

    private async Task<Stream?> SaveFileAsync(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Get(suggestedName.EndsWith(Alcifra.Presentation.WireNames.MessengerBackupExtension, StringComparison.Ordinal) ? "MsgBackUpToFile" : "MsgSaveAs"),
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
