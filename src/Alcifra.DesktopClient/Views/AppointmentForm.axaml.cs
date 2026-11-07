using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcifra.DesktopClient.Views;

/// <summary>An appointment's structured fields, shared by the editor window and the preview's card (see the markup).</summary>
public partial class AppointmentForm : UserControl
{
    public AppointmentForm() => AvaloniaXamlLoader.Load(this);
}
