using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

// The Compare-checkout modal (ADR 0517) — an inline unified diff of the current version vs the working copy in
// check-out, plus an optional Beyond Compare launch. Its VM is set as the DataContext by the caller after SetupAsync.
public partial class CompareCheckoutDialog : Window
{
    public CompareCheckoutDialog()
    {
        InitializeComponent();
    }

    public CompareCheckoutDialog(CompareCheckoutViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
