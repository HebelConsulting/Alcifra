using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Alcifra.DesktopClient.Views;

// Asked before a check-in (ADR 0952): the optional "why this version" note the new version carries. ShowDialog<string?>
// returns the trimmed comment — string.Empty when the user checks in without one — or null when they cancel, which
// aborts the check-in. A multi-document check-in asks once and gives every version the same comment.
public partial class CheckInCommentDialog : Window
{
    public CheckInCommentDialog()
    {
        InitializeComponent();
        Opened += (_, _) => CommentBox.Focus();
        CommentBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
            }
        };
    }

    private void OnCheckIn(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept() => Close(CommentBox.Text?.Trim() ?? string.Empty);
}
