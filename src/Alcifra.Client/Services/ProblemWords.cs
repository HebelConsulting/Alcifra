using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Localization;

namespace Alcifra.Client.Services;

/// <summary>
/// A refusal's words from its problem details: the machine code, and the facts the server sends as data beside it (the
/// <c>field</c> a required-field refusal names, #2217), composed in the reader's language — never the server's English
/// sentence (issue #424). Null where the body carries no code, so the caller says its own general sentence.
/// </summary>
public static class ProblemWords
{
    public static async Task<string?> ForAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            var code = Text(problem, "errorCode");
            return code is null ? null : ApiErrorText.For(code, Text(problem, "field"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement problem, string name) =>
        problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
