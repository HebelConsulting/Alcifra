using Avalonia.Controls;

namespace Alcifra.DesktopClient.Views;

// The "digitally signed" row badge (#491) — see the markup for why it is one control rather than two copies.
public partial class SignedBadge : UserControl
{
    public SignedBadge() => InitializeComponent();
}
