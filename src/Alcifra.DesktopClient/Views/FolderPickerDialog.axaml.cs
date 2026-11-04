using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

// Filing dialog (ADR "S3-backed inbox", phase 2; "Context-aware intray filing dialog"). ShowDialog<FilingResult?>
// returns the chosen target (a folder to file into, or a document to version), or null if cancelled / nothing
// selected.
public partial class FolderPickerDialog : Window
{
    public FolderPickerDialog()
    {
        InitializeComponent();

        // The dialog fills itself, so no caller can forget to: four of them did, and Move, Place reference and the
        // certificate folder each opened a picker with no folders in it (#2146).
        Opened += (_, _) => Safe.Fire(async () =>
        {
            if (DataContext is FolderPickerViewModel vm)
            {
                await vm.LoadAsync();
            }
        });
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Close(DataContext is FolderPickerViewModel vm ? vm.BuildResult() : null);
    }
}
