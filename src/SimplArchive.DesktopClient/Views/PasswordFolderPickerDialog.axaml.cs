using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The target folder of a move within a store (ADR 0968). ShowDialog<PasswordFolderInfo?> answers it, or null.
public partial class PasswordFolderPickerDialog : Window
{
    public PasswordFolderPickerDialog()
    {
        InitializeComponent();
    }

    public PasswordFolderPickerDialog(IReadOnlyList<PasswordFolderChoice> choices) : this()
    {
        Choices.ItemsSource = choices;
        Choices.DoubleTapped += (_, _) => Accept();
    }

    private void OnMove(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        if (Choices.SelectedItem is PasswordFolderChoice choice)
        {
            Close(choice.Folder);
        }
    }
}
