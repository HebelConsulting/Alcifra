using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.ApiClient;

public sealed partial class DocumentsClient
{
    /// <summary>A mail as the preview's card shows it (#2215, ADR 1073).</summary>
    /// <param name="Html">The body as sanitised HTML, for a client that shows it; null where the mail has none.</param>
    /// <param name="Text">The body as text, for a client that does not.</param>
    public sealed record MailCard(
        string Subject, string From, string To, string? Cc, DateTimeOffset? Date, string? Html, string Text, IReadOnlyList<MailAttachment> Attachments);

    /// <summary>An attachment, filed as a child document of the mail; its versions are how it is opened and saved.</summary>
    public sealed record MailAttachment(Guid Id, string Name, string? VersionsHref);

    /// <summary>The mail card at <paramref name="href"/> (the document's <c>email-card</c> rel), or null where it cannot be read.</summary>
    public async Task<MailCard?> ReadMailCardAsync(string href, CancellationToken cancellationToken = default)
    {
        using var response = await _core.Http.GetAsync(href, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var card = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new MailCard(
            ApiWire.StrOrNull(card, "subject") ?? string.Empty,
            ApiWire.StrOrNull(card, "from") ?? string.Empty,
            ApiWire.StrOrNull(card, "to") ?? string.Empty,
            ApiWire.StrOrNull(card, "cc"),
            card.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String ? date.GetDateTimeOffset() : null,
            ApiWire.StrOrNull(card, "html"),
            ApiWire.StrOrNull(card, "text") ?? string.Empty,
            card.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array
                ? [.. attachments.EnumerateArray().Select(a => new MailAttachment(
                    a.GetProperty("id").GetGuid(), ApiWire.StrOrNull(a, "name") ?? string.Empty, ApiCore.ParseLinks(a)?.Href("versions")))]
                : []);
    }
}
