using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// Gives a reference field's picker (#2214, ADR 1068) what it needs from the api client, which the field itself does not
/// hold: its candidates with their pictures, the current target, and the filing of a picked file. One place, so the detail
/// pane and the New dialog pick the same way.
/// </summary>
internal static class ReferencePickerWiring
{
    /// <param name="folderChildren">The address the item's folder files into, or null where there is none to file into.</param>
    /// <param name="current">The document the field points at today, as the index data resolved it.</param>
    public static void Wire(
        MaskFieldEditViewModel editor, AlcifraApiClient api, Func<CancellationToken, Task<string?>> folderChildren,
        DocumentsClient.IndexFieldTarget? current = null)
    {
        if (!editor.IsReferencePicker || editor.TargetsHref is not { } targetsHref)
        {
            return;
        }

        editor.TargetSource = async (typed, cancellationToken) =>
        {
            var choices = (await api.Masks.FieldTargetsAsync(targetsHref, typed, cancellationToken)).Select(t => new ReferenceChoice(t)).ToList();
            foreach (var choice in choices)
            {
                Safe.Fire(() => choice.LoadPictureAsync(api.Masks.TargetPictureAsync, cancellationToken));
            }

            return choices;
        };

        editor.FileFiler = async (fileName, bytes, maskId, cancellationToken) =>
        {
            if (await folderChildren(cancellationToken) is not { } childrenHref)
            {
                return null;
            }

            var id = await api.Documents.UploadFileAsync(childrenHref, fileName, bytes, comment: null, cancellationToken, maskId);
            var choice = new ReferenceChoice(new MasksClient.ReferenceTarget(id, Path.GetFileNameWithoutExtension(fileName), null));
            await choice.LoadPictureAsync((_, _) => Task.FromResult<byte[]?>(bytes), cancellationToken);
            return choice;
        };

        if (current is { Name: { Length: > 0 } name } && current.Links.FirstOrDefault(l => l.Rel == "document")?.Href is { } documentHref)
        {
            var chosen = new ReferenceChoice(new MasksClient.ReferenceTarget(current.Id, name, null, documentHref));
            editor.Chosen = chosen;
            Safe.Fire(() => chosen.LoadPictureAsync(api.Masks.TargetPictureAsync));
        }
    }
}
