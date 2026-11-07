using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcifra.DesktopClient.Views;

/// <summary>A contact's structured fields, shared by the editor window and the preview's card (see the markup).</summary>
public partial class ContactForm : UserControl
{
    public ContactForm() => AvaloniaXamlLoader.Load(this);
}
