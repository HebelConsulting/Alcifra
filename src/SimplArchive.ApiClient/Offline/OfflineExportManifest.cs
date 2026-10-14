using System.Text.Json;

namespace SimplArchive.ApiClient.Offline;

/// <summary>What an offline export's manifest says about how a device may keep it (core ADRs 0928/0929).</summary>
public static class OfflineExportManifest
{
    /// <summary>
    /// Whether the device may open each content envelope once and keep the content sealed to its copy's own key
    /// (ADR 0929, the Encryption Module's "open documents once per session"). False when the manifest does not say so,
    /// including every export from a core older than ABI 1.16: then content stays the envelope it arrived as.
    /// </summary>
    public static async Task<bool> ContentResealableOnDeviceAsync(IArchiveStorage export, CancellationToken cancellationToken = default)
    {
        if (await export.ReadAsync("manifest.json", cancellationToken) is not { } bytes)
        {
            return false;
        }

        using var manifest = JsonDocument.Parse(bytes);
        return manifest.RootElement.TryGetProperty("contentResealableOnDevice", out var value) && value.ValueKind == JsonValueKind.True;
    }
}
