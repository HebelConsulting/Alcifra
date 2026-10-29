using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Theming;

namespace Alcifra.DesktopClient.Views;

// About box (ADR 0504): the publisher's imprint and, in a white-label edition, the credit, both from the brand (ADR 0995),
// + the running client version, resolved from the same AssemblyInformationalVersion the auto-updater reads
// (ClientUpdate.RunningVersion).
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionBlock.Text = $"{Strings.Get("AboutVersion")} {ClientUpdate.RunningVersion}";

        var imprint = Brand.Shipped.Imprint;
        for (var i = 0; i < imprint.Lines.Count; i++)
        {
            ImprintPanel.Children.Add(Centred(imprint.Lines[i], i == 0 ? FontWeight.SemiBold : FontWeight.Normal));
        }

        var contact = Centred(imprint.Contact, FontWeight.Normal);
        contact.Margin = new Thickness(0, 2, 0, 0);
        ImprintPanel.Children.Add(contact);

        if (Brand.Shipped.Credit is { } credit)
        {
            var line = Centred(credit, FontWeight.Normal);
            line.Margin = new Thickness(0, 10, 0, 0);
            line.Opacity = 0.7;
            ImprintPanel.Children.Add(line);
        }
    }

    private static TextBlock Centred(string text, FontWeight weight) =>
        new() { Text = text, FontWeight = weight, HorizontalAlignment = HorizontalAlignment.Center };

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
