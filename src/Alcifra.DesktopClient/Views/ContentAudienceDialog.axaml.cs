using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// Who a Password Store or a Sealed document is encrypted for (ADR 0983): its members and managers, and the readers who
/// may be added. The dialog only EDITS; its answer is the whole audience to save, and the caller saves and re-seals it
/// in the same act — the dialog never holds a key.
/// </summary>
public partial class ContentAudienceDialog : Window
{
    // Parameterless ctor for the XAML designer/loader only.
    public ContentAudienceDialog()
    {
        InitializeComponent();
    }

    public ContentAudienceDialog(ContentAudienceInfo audience, string subject) : this()
    {
        DataContext = new AudienceEditor(audience);
        Subject.Text = subject;
    }

    private AudienceEditor? Editor => DataContext as AudienceEditor;

    private void OnAdd(object? sender, RoutedEventArgs e) => Editor?.AddSelected();

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudienceEditorRow row })
        {
            Editor?.Remove(row);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Editor is { CanSave: true } editor)
        {
            Close(editor.Result());
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>Fills the dialog for the headless screenshot hook: a store with a manager, a member, a group and a reader to add.</summary>
    internal void FillForShot()
    {
        Guid Id() => Guid.NewGuid();
        DataContext = new AudienceEditor(new ContentAudienceInfo(
            Id(), IsStore: true, IsPersonal: false, "\"shot\"", CanManage: true, CanAppointManager: false, IsMember: true,
            [new(Id(), null, "Anna Berger", true, true), new(Id(), null, "Tom Weber", false, true), new(null, Id(), "Finance", false, true),
             new(Id(), null, "Lena Graf", false, false)],
            [], [new(Id(), null, "Mihaela Ionescu"), new(null, Id(), "Operations")], LinkMap.Empty));
        Subject.Text = "Finance / Team passwords";
    }
}
