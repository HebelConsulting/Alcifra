using System.Net.Http.Json;
using Alcifra.Client.Dialogs;
using Alcifra.Client.Models;
using Alcifra.Localization;
using MudBlazor;

namespace Alcifra.Client.Services;

/// <summary>
/// "Inspect seal…" on a row (#2291): follows the document's <c>seal-inspection</c> rel and shows the server's answer. Its own
/// service rather than another member of DocumentActions, which is at the line limit; the desktop's twin is the window's
/// OnInspectSeal.
/// </summary>
public sealed class SealInspections(HttpClient http, IDialogService dialogs, ISnackbar snackbar)
{
    public async Task OpenAsync(BrowseNode node)
    {
        if (DocumentActions.DocumentAddress(node) is not { } address)
        {
            return;
        }

        try
        {
            var document = await http.GetFromJsonAsync<LinksOnly>(address);
            if (Hypermedia.Links.Href(document?.Links, "seal-inspection") is not { } href
                || await http.GetFromJsonAsync<SealInspection>(href) is not { } inspection)
            {
                snackbar.Add(Strings.Get("SealLoadFailed"), Severity.Warning);
                return;
            }

            var parameters = new DialogParameters { ["Inspection"] = inspection, ["Name"] = node.Name };
            await dialogs.ShowAsync<SealInspectionDialog>(string.Format(Strings.Get("SealTitle"), node.Name), parameters,
                new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Medium, FullWidth = true });
        }
        catch (HttpRequestException)
        {
            snackbar.Add(Strings.Get("SealLoadFailed"), Severity.Warning);
        }
    }

    // The document resource, as much of it as this needs.
    private sealed class LinksOnly
    {
        public List<Hypermedia.LinkResponse>? Links { get; set; }
    }
}
