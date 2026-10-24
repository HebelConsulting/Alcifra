using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// An opened password entry in the preview pane (ADR 0968). The code-behind does the one thing only the view knows: the
// window losing focus, which hides every secret held open (the core's SecretReveal: "the window lost focus").
public partial class PasswordEntryView : UserControl
{
    private Window? _window;

    public PasswordEntryView() => AvaloniaXamlLoader.Load(this);

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

        (DataContext as PasswordEntryPreviewViewModel)?.HideAll();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDeactivated(object? sender, EventArgs e) => (DataContext as PasswordEntryPreviewViewModel)?.HideAll();
}
