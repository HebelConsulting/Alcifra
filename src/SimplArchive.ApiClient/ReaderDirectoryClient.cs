using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>
/// Who an audience can be sealed to (ADR 0976): the tenant's readers and the certificates that address them, followed
/// from the root's <c>readerDirectory</c> rel; and the core's decision on a certificate pasted with its attestation chain,
/// at <c>readerDirectoryValidation</c>.
/// </summary>
/// <remarks>
/// A sealed document's audience is chosen ON THE DEVICE and sealed there — the server never sees it (ADR 0975) — so this
/// client is the one that must obtain the certificates, from the core's own answer rather than a separate idea of who
/// can read.
/// </remarks>
public sealed class ReaderDirectoryClient(ApiCore core)
{
    /// <summary>One person an audience can be sealed to.</summary>
    public sealed record Reader(Guid UserId, string DisplayName, IReadOnlyList<Certificate> Certificates);

    /// <summary>One certificate of a reader's set; <see cref="CertificatePem"/> is what an envelope is sealed to.</summary>
    public sealed record Certificate(
        string Label, string Subject, string? Issuer, string Serial, string Thumbprint,
        DateTimeOffset? NotAfter, string CertificatePem);

    /// <summary>One page of the directory, and where the next one is (null on the last page).</summary>
    public sealed record Page(IReadOnlyList<Reader> Readers, string? NextHref);

    /// <summary>
    /// The first page of readers, optionally filtered by a display-name fragment; null where this installation
    /// advertises no directory (an older server).
    /// </summary>
    public async Task<Page?> GetAsync(string? search = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (!(await core.GetRootLinksAsync(cancellationToken)).TryGetValue("readerDirectory", out var href))
        {
            return null;
        }

        return await ReadPageAsync(AddQuery(href, search, limit), cancellationToken);
    }

    /// <summary>
    /// Every page of the directory — for a picker that shows the whole tenant at once. Null where this installation
    /// advertises no directory (an older server), which is a different answer from "nobody holds a key".
    /// </summary>
    public async Task<IReadOnlyList<Reader>?> GetAllAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(search, cancellationToken: cancellationToken);
        if (page is null)
        {
            return null;
        }

        var readers = new List<Reader>(page.Readers);
        while (page.NextHref is { } next)
        {
            page = await ReadPageAsync(next, cancellationToken);
            readers.AddRange(page.Readers);
        }

        return readers;
    }

    /// <summary>
    /// Asks the core whether a certificate may be sealed to, with the key's attestation as read from the token (ADR
    /// 0963). The response is returned as-is: 200 carries the certificate to seal to, and a refusal is the core's own
    /// problem document, whose code and message the dialog shows.
    /// </summary>
    /// <returns>Null where this installation advertises no validation address (an older server).</returns>
    public async Task<HttpResponseMessage?> ValidateAsync(
        string certificatePem, EnrolledCertificatesClient.KeyAttestation? attestation, CancellationToken cancellationToken = default)
    {
        if (!(await core.GetRootLinksAsync(cancellationToken)).TryGetValue("readerDirectoryValidation", out var href))
        {
            return null;
        }

        var body = new Dictionary<string, object?> { ["certificatePem"] = certificatePem };
        if (attestation is not null)
        {
            body["attestation"] = attestation;
        }

        return await core.Http.PostAsJsonAsync(href, body, cancellationToken);
    }

    /// <summary>Reads a 200 validation answer into the certificate the dialog may seal to.</summary>
    public static Certificate ParseValidation(JsonElement answer) =>
        ParseCertificate(answer.GetProperty("certificate"));

    private async Task<Page> ReadPageAsync(string href, CancellationToken cancellationToken)
    {
        var json = await core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        var readers = json.TryGetProperty("readers", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(ParseReader).ToList()
            : [];
        return new Page(readers, Link(json, "next"));
    }

    private static Reader ParseReader(JsonElement row) => new(
        row.GetProperty("userId").GetGuid(),
        row.GetProperty("displayName").GetString() ?? string.Empty,
        row.TryGetProperty("certificates", out var certificates) && certificates.ValueKind == JsonValueKind.Array
            ? certificates.EnumerateArray().Select(ParseCertificate).ToList()
            : []);

    private static Certificate ParseCertificate(JsonElement row) => new(
        Text(row, "label") ?? string.Empty,
        Text(row, "subject") ?? string.Empty,
        Text(row, "issuer"),
        Text(row, "serial") ?? string.Empty,
        Text(row, "thumbprint") ?? string.Empty,
        DateTimeOffset.TryParse(Text(row, "notAfter"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var notAfter) ? notAfter : null,
        Text(row, "certificatePem") ?? string.Empty);

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Appending a QUERY to an advertised href is following; appending a path segment is composing (ADR 0557).
    private static string AddQuery(string href, string? search, int? limit)
    {
        var query = new List<string>(2);
        if (search is { Length: > 0 })
        {
            query.Add($"search={Uri.EscapeDataString(search)}");
        }

        if (limit is { } take)
        {
            query.Add($"limit={take}");
        }

        if (query.Count == 0)
        {
            return href;
        }

        return href + (href.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join("&", query);
    }

    private static string? Link(JsonElement resource, string rel) =>
        resource.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray()
                .FirstOrDefault(l => l.TryGetProperty("rel", out var r) && r.GetString() == rel) is { ValueKind: JsonValueKind.Object } found
                ? found.GetProperty("href").GetString()
                : null
            : null;
}
