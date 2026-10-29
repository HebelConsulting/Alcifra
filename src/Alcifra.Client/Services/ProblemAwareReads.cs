using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Localization;

namespace Alcifra.Client.Services;

/// <summary>Reads a body the way <c>GetByteArrayAsync</c> does, but keeps a refusal's reason.</summary>
/// <remarks>
/// <c>GetByteArrayAsync</c> throws on a non-success status with the status only, so a refusal the user must
/// read — the strict tier's <c>PLAINTEXT_CONTENT_REFUSED</c> on an export (ADR 0829) — surfaced as "409".
/// </remarks>
public static class ProblemAwareReads
{
    public static async Task<byte[]> GetBytesOrRefusalAsync(this HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                code = problem.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
            }
            catch (JsonException)
            {
                // Not a problem body (a proxy's error page): the generic sentence below.
            }

            throw new ServerRefusedException(ApiErrorText.For(code));
        }

        return await response.Content.ReadAsByteArrayAsync();
    }
}

/// <summary>The server refused a read; the message is the localized reason.</summary>
public sealed class ServerRefusedException(string message) : Exception(message);
