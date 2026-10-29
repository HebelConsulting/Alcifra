using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

// The ordered OCR-language multi-select picker (ADR "System fields + OCR-language mask field"). ShowDialog
// returns the selected codes in priority order, or null if cancelled.
public partial class OcrLanguagePickerDialog : Window
{
    public OcrLanguagePickerDialog()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close(DataContext is OcrLanguagePickerViewModel vm ? vm.OrderedCodes() : (List<string>?)null);
}
