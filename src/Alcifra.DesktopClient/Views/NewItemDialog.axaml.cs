using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The New dialog for a kind with required fields (#2217, ADR 1067): the form is its <see cref="NewItemViewModel"/>, and
/// the dialog closes with that form when the user creates, or with null. Nothing is created here; the caller posts once.
/// </summary>
public partial class NewItemDialog : Window
{
    public NewItemDialog()
        : this(new NewItemViewModel { Title = string.Empty })
    {
    }

    public NewItemDialog(NewItemViewModel form)
    {
        InitializeComponent();
        DataContext = form;
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>The name completes from the server's vocabulary (ABI 0.21), as the plain name prompt does.</summary>
    public void CompleteFrom(Func<string?, CancellationToken, Task<IEnumerable<object>>> populator) =>
        NameBox.AsyncPopulator = populator;

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NewItemViewModel { CanCreate: true } form)
        {
            Close(form);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
