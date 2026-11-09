using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcifra.DesktopClient.Views;

/// <summary>One secret field's row, drawn alike in every preview that shows one (ADR 0968, ADR 1084).</summary>
public partial class SecretFieldRow : UserControl
{
    public SecretFieldRow() => AvaloniaXamlLoader.Load(this);
}
