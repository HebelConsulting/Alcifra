using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcifra.DesktopClient.Views;

// The Tube tab's view (#2148), its own UserControl like the Contacts tab.
public partial class TubeTab : UserControl
{
    public TubeTab() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
