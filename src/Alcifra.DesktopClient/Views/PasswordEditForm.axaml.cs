using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

// The create and edit form of a password entry (ADR 0968). Its keys (Cmd/Ctrl+S saves, Esc cancels) are the tab's, which
// handles them for the form wherever focus is inside it; leaving the screen lets go of every held eye.
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
}
