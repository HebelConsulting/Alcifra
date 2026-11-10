using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// What a sealed file is and who can open it (#2291). Its answer is whether Re-seal was asked for; the caller re-seals,
/// because the dialog holds no key.
/// </summary>
public partial class SealInspectionDialog : Window
{
    // Parameterless ctor for the XAML designer/loader only.
    public SealInspectionDialog()
    {
        InitializeComponent();
    }

    public SealInspectionDialog(SealInspectionViewModel inspection, string title, string subject) : this()
    {
        DataContext = inspection;
        Title = title;
        Subject.Text = subject;
    }

    private void OnReseal(object? sender, RoutedEventArgs e) => Close(true);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
