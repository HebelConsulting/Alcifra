using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

// A CI/CD secret in the preview pane (ADR 1084). As for a password entry, the one thing only the view knows is the window
// losing focus, which hides every value held open.
public partial class CiCdEntryView : UserControl
{
    private Window? _window;

    public CiCdEntryView() => AvaloniaXamlLoader.Load(this);

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

        (DataContext as CiCdEntryPreviewViewModel)?.HideAll();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDeactivated(object? sender, EventArgs e) => (DataContext as CiCdEntryPreviewViewModel)?.HideAll();
}
