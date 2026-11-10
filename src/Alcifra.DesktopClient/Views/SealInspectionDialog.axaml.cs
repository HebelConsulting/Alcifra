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

    private SealInspectionViewModel? Inspection => DataContext as SealInspectionViewModel;

    private void OnTryMine(object? sender, RoutedEventArgs e) => Services.Safe.Fire(async () =>
    {
        if (Inspection is { } inspection)
        {
            await inspection.TryMineAsync();
        }
    });

    // A key file: a PKCS#12 file asks for its password only when it needs one; a PEM must hold the private key too.
    private void OnTryFile(object? sender, RoutedEventArgs e) => Services.Safe.Fire(async () =>
    {
        if (Inspection is not { } inspection
            || (await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { AllowMultiple = false })) is not [var picked])
        {
            return;
        }

        byte[] bytes;
        await using (var stream = await picked.OpenReadAsync())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        try
        {
            using var key = await LoadAsync(bytes, picked.Name);
            if (key is not null)
            {
                await inspection.TryKeyAsync(key);
            }
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            inspection.KeyUnreadable(ex.Message);
        }
    });

    private async Task<System.Security.Cryptography.X509Certificates.X509Certificate2?> LoadAsync(byte[] bytes, string name)
    {
        try
        {
            return Alcifra.HardwareKeys.KeyTry.LoadKeyFile(bytes, null);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            var prompt = new KeyFilePasswordDialog(name);
            await prompt.ShowDialog(this);
            return prompt.Password is { } password ? Alcifra.HardwareKeys.KeyTry.LoadKeyFile(bytes, password) : null;
        }
    }

    private void OnReseal(object? sender, RoutedEventArgs e) => Close(true);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}
