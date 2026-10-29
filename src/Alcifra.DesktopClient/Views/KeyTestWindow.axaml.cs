using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.ApiClient.KeyTest;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// <i>Test the hardware key</i> (core #1864, ADR 0955), opened from the logon window's tools icon and the main
/// window's title bar.
/// </summary>
/// <remarks>
/// Opened through <see cref="ShowAsync"/> rather than from <c>MainWindow</c>'s code-behind, which is on the
/// 1000-line debt list: the title bar and the logon window both call this one place.
/// </remarks>
public partial class KeyTestWindow : Window
{
    // Parameterless ctor for the XAML designer/loader only.
    public KeyTestWindow()
    {
        InitializeComponent();
    }

    public KeyTestWindow(KeyTestViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.CopyToClipboard = async text =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        };
    }

    /// <summary>The window's model: the PKCS#11 port, the PIN dialog owned by the window, and the archive side when
    /// <paramref name="api"/> is a signed-in session.</summary>
    public static KeyTestWindow For(AlcifraApiClient? api)
    {
        KeyTestWindow? window = null;
        var test = new HardwareKeyTest(
            new Pkcs11KeyPort(),
            async card =>
            {
                var dialog = new CardPinDialog(card);
                await dialog.ShowDialog(window!);
                return dialog.Pin;
            },
            api is null ? null : thumbprints => GatherAsync(api, thumbprints),
            ClientUpdate.RunningVersion);
        window = new KeyTestWindow(new KeyTestViewModel(test, signedIn: api is not null));
        return window;
    }

    /// <summary>Opens the test over <paramref name="owner"/>; <paramref name="api"/> is null before sign-in.</summary>
    public static Task ShowAsync(Window owner, AlcifraApiClient? api) => For(api).ShowDialog(owner);

    private static async Task<KeyTestArchiveSide?> GatherAsync(AlcifraApiClient api, System.Collections.Generic.IReadOnlyList<string> thumbprints) =>
        await KeyTestArchiveProbe.GatherAsync(api.Core, api.Profile, thumbprints);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
