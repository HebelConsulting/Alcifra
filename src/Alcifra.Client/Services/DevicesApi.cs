using System.Net.Http.Json;
using Alcifra.Localization;

namespace Alcifra.Client.Services;

/// <summary>
/// The user's protocol devices for the web client (#1970 slice 3): the same resource the desktop's DevicesClient reads,
/// reached by <c>me</c>'s <c>protocolDevices</c> rel and the rels each answer carries (ADR 0543). The web cannot take the
/// shared API client (it is not WASM-sized), so it mirrors it here; what a row SAYS comes from
/// <see cref="Alcifra.Presentation.ProtocolDeviceText"/>, as on the desktop.
/// </summary>
public sealed class DevicesApi(HttpClient http, ApiRoot apiRoot)
{
    public sealed record LinkDto(string Rel, string Href, string? Method);

    public sealed record ServersDto(
        string Username, bool ImapAvailable, string? ImapHost, int? ImapPort, int? ImapTlsPort, string OutgoingHost,
        string WebDavUrl, string CalDavUrl, string CardDavUrl);

    public sealed record DeviceDto(
        string Name, string Kind, bool Mail, bool Files, bool Calendars, bool Contacts, bool EncryptMail, bool EncryptMailLocked,
        DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool CanEdit, bool CanDelete, string ETag, List<LinkDto>? Links)
    {
        public string? Self => Links?.FirstOrDefault(l => l.Rel == "self")?.Href;
    }

    public sealed record DeviceListDto(
        List<DeviceDto> Items, bool EncryptMailLocked, bool ShowAllDocuments, ServersDto Servers, List<LinkDto>? Links)
    {
        public bool ReachesFiles => Items.Any(d => d.Files);

        public string? Href(string rel) => Links?.FirstOrDefault(l => l.Rel == rel)?.Href;
    }

    public sealed record HandOverDto(string? ProfileUrl, string? ProfileQr, DateTimeOffset? ProfileExpiresAt, string PasswordQr, ServersDto Servers);

    public sealed record EnrolledDto(
        string Name, string Kind, bool Mail, bool Files, bool Calendars, bool Contacts, bool EncryptMail, string Password,
        string Username, HandOverDto HandOver);

    public sealed record DeviceRequest(string Name, bool Mail, bool Files, bool Calendars, bool Contacts, bool EncryptMail);

    private sealed record ProblemResponse { public string? ErrorCode { get; set; } }

    public async Task<DeviceListDto> GetAsync() =>
        (await http.GetFromJsonAsync<DeviceListDto>(await apiRoot.RequireMeAsync("protocolDevices")))!;

    public async Task<EnrolledDto> EnrolAsync(DeviceListDto list, DeviceRequest request)
    {
        using var response = await http.PostAsJsonAsync(list.Href("self")!, request);
        await ThrowIfRefusedAsync(response, "DvEnrolFailed");
        return (await response.Content.ReadFromJsonAsync<EnrolledDto>())!;
    }

    public async Task UpdateAsync(DeviceDto device, DeviceRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, device.Self) { Content = JsonContent.Create(request) };
        message.Headers.TryAddWithoutValidation("If-Match", device.ETag);
        using var response = await http.SendAsync(message);
        await ThrowIfRefusedAsync(response, "DvChangeFailed");
    }

    public async Task RevokeAsync(DeviceDto device)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, device.Self);
        message.Headers.TryAddWithoutValidation("If-Match", device.ETag);
        using var response = await http.SendAsync(message);
        await ThrowIfRefusedAsync(response, "DvRevokeFailed");
    }

    public async Task SetShowAllDocumentsAsync(DeviceListDto list, bool showAllDocuments)
    {
        using var response = await http.PutAsJsonAsync(list.Href("settings")!, new { showAllDocuments });
        await ThrowIfRefusedAsync(response, "DvChangeFailed");
    }

    // The refusal's errorCode in the user's language (issue #424), else the act's own message.
    private static async Task ThrowIfRefusedAsync(HttpResponseMessage response, string fallbackKey)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        try
        {
            code = (await response.Content.ReadFromJsonAsync<ProblemResponse>())?.ErrorCode;
        }
        catch (Exception)
        {
            // Not a problem document: the act's own message says it failed.
        }

        throw new DeviceRefusedException(code is not null ? ApiErrorText.For(code) : Strings.Get(fallbackKey));
    }
}

/// <summary>A device act the API refused, with its reason in the user's language.</summary>
public sealed class DeviceRefusedException(string message) : Exception(message);
