using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.ApiClient;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Certificate enrolment's two tenant settings (ADR 0936), followed from the root's <c>certificateSettings</c> rel: present
/// only while a module licensing enrolment is active, and readable only by a module administrator.
/// </summary>
public sealed class CertificateSettingsClient(ApiCore core)
{
    /// <summary>The settings as read, with the address and tag a save sends back.</summary>
    public sealed record CertificateSettings(bool SelfServiceEnabled, Guid? ParentFolderId, string? ParentFolderName, string Href, string? ETag);

    /// <summary>The settings, or null where they are not offered or not the caller's to see.</summary>
    public async Task<CertificateSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!(await core.GetRootLinksAsync(cancellationToken)).TryGetValue("certificateSettings", out var href))
        {
            return null;
        }

        using var response = await core.Http.GetAsync(href, cancellationToken);
        return response.IsSuccessStatusCode
            ? Parse(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), href, response.Headers.ETag?.Tag)
            : null;   // 403: the caller does not administer modules
    }

    /// <summary>Saves the whole value with the tag of the read that filled the form (ADR 0794).</summary>
    public async Task<CertificateSettings> SaveAsync(CertificateSettings settings, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, settings.Href)
        {
            Content = JsonContent.Create(new { selfServiceEnabled = settings.SelfServiceEnabled, parentFolderId = settings.ParentFolderId }),
        };
        if (settings.ETag is { } tag)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        using var response = await core.Http.SendAsync(request, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The certificate settings could not be saved.", cancellationToken);
        return Parse(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), settings.Href, response.Headers.ETag?.Tag);
    }

    private static CertificateSettings Parse(JsonElement json, string href, string? etag) => new(
        json.TryGetProperty("selfServiceEnabled", out var on) && on.ValueKind == JsonValueKind.True,
        json.TryGetProperty("parentFolderId", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetGuid() : null,
        json.TryGetProperty("parentFolderName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
        href,
        etag);
}
