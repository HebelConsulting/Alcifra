using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>The Repositories tab's detail pane (#519 tranche 8) — index data over (preview | chat).</summary>
/// <remarks>
/// AvaloniaXamlLoader.Load is correct here, unlike the tree and contents panes: the one named control, the chat's
/// CommentBox, is looked up by name when needed rather than through a generated field, which this loader would
/// leave null.
/// </remarks>
public partial class DocumentDetailPane : UserControl
{
    private MainWindowViewModel? _watched;

    public DocumentDetailPane()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Watch(DataContext as MainWindowViewModel);
    }

    private void Watch(MainWindowViewModel? vm)
    {
        if (_watched is not null)
        {
            _watched.MentionInserted -= CaretAfterMention;
        }

        _watched = vm;
        if (vm is not null)
        {
            vm.MentionInserted += CaretAfterMention;
        }
    }

    // After the binding has written the new text: the caret at its end and the focus back in the box, so typing
    // continues after the mention instead of inside it.
    private void CaretAfterMention() => Dispatcher.UIThread.Post(() =>
    {
        if (this.FindControl<TextBox>("CommentBox") is { } box)
        {
            box.CaretIndex = box.Text?.Length ?? 0;
            box.Focus();
        }
    });

    private void OnWorkflowTransition(object? sender, RoutedEventArgs e) => Window()?.OnWorkflowTransition(sender, e);

    private void OnEditOcrLanguages(object? sender, RoutedEventArgs e) => Window()?.OnEditOcrLanguages(sender, e);

    // Null in the headless screenshot renders, which host panes without a window.
    private MainWindow? Window() => TopLevel.GetTopLevel(this) as MainWindow;
}
