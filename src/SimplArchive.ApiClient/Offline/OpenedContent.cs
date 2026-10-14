using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimplArchive.ApiClient.Offline;

/// <summary>
/// An offline export whose content envelopes the device opened once (core ADR 0929): where the manifest allows it
/// (<see cref="OfflineExportManifest.ContentResealableOnDeviceAsync"/>), the device opens every envelope of an update
/// with the user's key in one go and keeps the content sealed to its copy's own key, so a copy unlocked for the
/// session opens documents without asking again.
/// </summary>
/// <remarks>
/// Like <see cref="SealedExport"/>: the envelopes are handed out first, opened by the host (only the key differs per
/// device), and put back; <see cref="ArchiveMerger"/> then merges the opened export as any other. A version whose
/// envelope was opened names the content's own hash and real type, so the responder's hash check and its content
/// type hold for the plaintext; the envelope itself is not kept.
/// </remarks>
public static class OpenedContent
{
    public const string EnvelopeType = "application/pkcs7-mime";

    /// <summary>The export's content envelopes by blob reference: every version row that travelled as an envelope.</summary>
    public static async Task<IReadOnlyDictionary<string, byte[]>> EnvelopesAsync(IArchiveStorage export, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await RowsAsync(export, cancellationToken))
        {
            if (IsEnvelope(row) && row["blobRef"]?.GetValue<string>() is { } blobRef && !result.ContainsKey(blobRef)
                && await export.ReadAsync($"blobs/{blobRef}", cancellationToken) is { } envelope)
            {
                result[blobRef] = envelope;
            }
        }

        return result;
    }

    /// <summary>The export with its envelopes opened: each version names its content's hash and real type.</summary>
    /// <param name="opened">What the user's key returned for <see cref="EnvelopesAsync"/>, by the same blob reference.</param>
    public static async Task<IArchiveStorage> WithOpenedAsync(IArchiveStorage export, IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> opened,
        CancellationToken cancellationToken = default)
    {
        var blobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var row in await RowsAsync(export, cancellationToken))
        {
            if (IsEnvelope(row) && row["blobRef"]?.GetValue<string>() is { } blobRef && opened.TryGetValue(blobRef, out var content))
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(content.Bytes));
                row["blobRef"] = sha;
                row["contentType"] = content.ContentType;
                blobs[sha] = content.Bytes;
            }

            lines.Add(row.ToJsonString());
        }

        return new Opened(export, Encoding.UTF8.GetBytes(string.Join('\n', lines)), blobs);
    }

    private static bool IsEnvelope(JsonObject row) =>
        string.Equals(row["contentType"]?.GetValue<string>(), EnvelopeType, StringComparison.OrdinalIgnoreCase);

    private static async Task<List<JsonObject>> RowsAsync(IArchiveStorage export, CancellationToken cancellationToken) =>
        await export.ReadAsync("tree/versions.jsonl", cancellationToken) is { } bytes
            ? [.. Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line) as JsonObject ?? throw new JsonException("A version row is not an object."))]
            : [];

    private sealed class Opened(IArchiveStorage export, byte[] versions, IReadOnlyDictionary<string, byte[]> blobs) : IArchiveStorage
    {
        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            path == "tree/versions.jsonl" ? Task.FromResult<byte[]?>(versions)
            : path.StartsWith("blobs/", StringComparison.Ordinal) && blobs.TryGetValue(path["blobs/".Length..], out var plain) ? Task.FromResult<byte[]?>(plain)
            : export.ReadAsync(path, cancellationToken);
    }
}
