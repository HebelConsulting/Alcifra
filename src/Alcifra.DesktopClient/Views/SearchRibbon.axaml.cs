using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The Search tab's ribbon (review round after #530): pure markup — all three buttons bind to the
/// view-model directly, so there is nothing to forward here.
/// </summary>
public partial class SearchRibbon : UserControl
{
    public SearchRibbon() => AvaloniaXamlLoader.Load(this);
}
