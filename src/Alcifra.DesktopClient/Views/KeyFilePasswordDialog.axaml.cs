using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Alcifra.DesktopClient.Views;

// The password of a key file, asked when a PKCS#12 file needs one (#2291). What the caller gets back is used once, to load
// the key for a try, and then dropped; nothing here keeps it.
public partial class KeyFilePasswordDialog : Window
{
    /// <summary>What the user typed, or null if they cancelled.</summary>
    public string? Password { get; private set; }

    // Parameterless ctor for the XAML designer/loader only.
    public KeyFilePasswordDialog()
    {
        InitializeComponent();
    }

    public KeyFilePasswordDialog(string fileName) : this()
    {
        Title = string.Format(CultureInfo.CurrentCulture, Alcifra.Localization.Strings.Get("SealKeyPassword"), fileName);
        FileLine.Text = fileName;
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Text;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Password = null;
        Close();
    }
}
