using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

// The Remind… dialog (ADR "Document reminders"). Loads its target list + pending reminders when opened.
public partial class ReminderDialog : Window
{
    // Parameterless ctor so the Avalonia XAML runtime loader can reach this window (AVLN3001).
    public ReminderDialog() : this(null)
    {
    }

    public ReminderDialog(ReminderDialogViewModel? viewModel)
    {
        InitializeComponent();
        if (viewModel is not null)
        {
            DataContext = viewModel;
            Opened += (_, _) => Safe.Fire(() => viewModel.LoadAsync());
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
