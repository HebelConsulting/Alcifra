using Alcifra.Localization;
using Microsoft.JSInterop;
using MudBlazor;

namespace Alcifra.Client.Services;

/// <summary>
/// Saves a version's annotated-PDF export (ADR 1106). The export is an API route behind the bearer token, so a
/// plain navigation cannot fetch it: the bytes are read with the authenticated client and handed to the browser
/// to save — exactly as an archive entry is (its entries are proxied for the same reason). A named service rather
/// than a method on <see cref="DocumentActions"/>, whose file already sits at the 1,000-line line ceiling.
/// </summary>
public static class AnnotatedPdfDownload
{
    public static async Task SaveAsync(HttpClient http, IJSRuntime js, ISnackbar snackbar, string url, string fallbackName)
    {
        try
        {
            using var response = await http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var suggested = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"{fallbackName} (annotations).pdf";
            using var stream = new MemoryStream(bytes);
            using var streamRef = new DotNetStreamReference(stream);
            await js.InvokeVoidAsync("downloadFileFromStream", suggested, streamRef);
        }
        catch (Exception ex)
        {
            snackbar.Add(string.Format(Strings.Get("StErrAnnotatedPdf"), ex.Message), Severity.Error);
        }
    }
}
