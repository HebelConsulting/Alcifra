using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alcifra.DesktopClient.Views;

// A small yes/no confirmation modal. ShowDialog<bool> returns true if confirmed. See ADR "Desktop
// delete/rename".
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Are you sure?", "OK")
    {
    }

    public ConfirmDialog(string message, string confirmLabel = "OK")
    {
        InitializeComponent();
        MessageBlock.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    /// <summary>A message to read and close: one button, no question (the Messages tab's "Read by…", ADR 0967).</summary>
    public static ConfirmDialog Notice(string title, string message, string closeLabel)
    {
        var dialog = new ConfirmDialog(message, closeLabel) { Title = title };
        dialog.CancelButton.IsVisible = false;
        return dialog;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
