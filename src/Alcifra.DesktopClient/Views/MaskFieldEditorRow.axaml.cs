using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcifra.DesktopClient.Views;

/// <summary>One index field's editor row, typed to its <c>MaskFieldEditViewModel</c> (see the markup).</summary>
public partial class MaskFieldEditorRow : UserControl
{
    public MaskFieldEditorRow() => AvaloniaXamlLoader.Load(this);
}
