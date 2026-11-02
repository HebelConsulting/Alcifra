using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Localization;

namespace Alcifra.ApiClient;

/// <summary>Every address a device is set up with, and the user name all of them take (#1970).</summary>
public sealed record DeviceServers(
    string Username, bool ImapAvailable, string? ImapHost, int? ImapPort, int? ImapTlsPort, string OutgoingHost,
    string WebDavUrl, string CalDavUrl, string CardDavUrl);

/// <summary>One device that reaches mail, files, calendars or contacts with a password of its own (#1970).</summary>
/// <param name="Kind"><c>enrolled</c>; <c>earlierDav</c> / <c>earlierImap</c> for a password from before enrolment;
/// <c>seeded</c> for the demo seed's device (ADR 1030).</param>
public sealed record ProtocolDevice(
    string Name, string Kind, bool Mail, bool Files, bool Calendars, bool Contacts, bool EncryptMail, bool EncryptMailLocked,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool CanEdit, bool CanDelete, string ETag, LinkMap? Links,
    bool CollectionsChosen = false, IReadOnlyList<Guid>? ChosenCollections = null)
{
    /// <summary>Whether its calendars and address books can be chosen: the server advertised the <c>collections</c> rel (#2090).</summary>
    public bool OffersCollections => Links?.Href("collections") is not null;

    public bool IsEarlier => Kind is "earlierDav" or "earlierImap";

    /// <summary>The demo seed's device (ADR 1030), whose name carries the demo password.</summary>
    public bool IsSeeded => Kind == "seeded";
}

/// <summary>The user's devices, the addresses they reach, and the user's mail view (one setting for every device).</summary>
public sealed record DeviceList(
    IReadOnlyList<ProtocolDevice> Items, bool EncryptMailLocked, bool ShowAllDocuments, DeviceServers Servers, LinkMap? Links)
{
    /// <summary>Whether any device reaches files: what decides whether WebDAV can be mounted with a password.</summary>
    public bool ReachesFiles => Items.Any(d => d.Files);
}

/// <summary>What a new device is set up with (#1970 slice 2): QR codes are PNG data URLs; the profile link works once.</summary>
public sealed record DeviceHandOver(string? ProfileUrl, string? ProfileQr, DateTimeOffset? ProfileExpiresAt, string PasswordQr, DeviceServers Servers);

/// <summary>A device just enrolled: its password, shown this once, and its hand-over.</summary>
public sealed record EnrolledDevice(ProtocolDevice Device, string Password, string Username, DeviceHandOver HandOver);

/// <summary>A device in full, as enrolment and a change state it (no PATCH, ADR 0872).</summary>
/// <param name="CollectionsChosen">All (false) or chosen (true) calendars and address books (#2090); null states nothing.</param>
/// <param name="ChosenCollections">The ticked shared collections; null states nothing.</param>
public sealed record DeviceRequest(string Name, bool Mail, bool Files, bool Calendars, bool Contacts, bool EncryptMail,
    bool? CollectionsChosen = null, IReadOnlyList<Guid>? ChosenCollections = null);

/// <summary>A shared calendar or address book a device can tick (#2090).</summary>
/// <param name="Kind"><c>calendar</c> or <c>contacts</c>.</param>
public sealed record DeviceCollectionChoice(Guid Id, string Name, string Kind, bool Ticked)
{
    public bool IsCalendar => Kind == "calendar";
}

/// <summary>
/// The user's protocol devices (#1970), reached by <c>me</c>'s <c>protocolDevices</c> rel and then by the rels each answer
/// carries (ADR 0543): the collection lists and enrols, its <c>settings</c> sets the mail view, a device's <c>self</c> is
/// changed and revoked by the method, with its ETag. A refusal surfaces as an <see cref="ApiActionException"/> in the
/// user's language (its <c>errorCode</c>, never the English detail).
/// </summary>
public sealed class DevicesClient(ApiCore core, ProfileClient profile)
{
    public async Task<DeviceList> GetAsync(CancellationToken cancellationToken = default) =>
        ParseList(await core.Http.GetFromJsonAsync<JsonElement>(await profile.MeHrefAsync("protocolDevices", cancellationToken), cancellationToken));

    public async Task<EnrolledDevice> EnrolAsync(DeviceList list, DeviceRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PostAsJsonAsync(Self(list.Links), Body(request), cancellationToken);
        await ApiWire.ThrowIfProblemAsync(response, Strings.Get("DvEnrolFailed"), cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var handOver = json.GetProperty("handOver");
        return new EnrolledDevice(
            ParseDevice(json),
            json.GetProperty("password").GetString() ?? string.Empty,
            json.GetProperty("username").GetString() ?? string.Empty,
            new DeviceHandOver(
                ApiWire.StrOrNull(handOver, "profileUrl"),
                ApiWire.StrOrNull(handOver, "profileQr"),
                handOver.TryGetProperty("profileExpiresAt", out var expires) && expires.ValueKind == JsonValueKind.String ? expires.GetDateTimeOffset() : null,
                handOver.GetProperty("passwordQr").GetString() ?? string.Empty,
                ParseServers(handOver.GetProperty("servers"))));
    }

    public async Task UpdateAsync(ProtocolDevice device, DeviceRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, Self(device.Links)) { Content = JsonContent.Create(Body(request)) };
        message.Headers.TryAddWithoutValidation("If-Match", device.ETag);
        using var response = await core.Http.SendAsync(message, cancellationToken);
        await ApiWire.ThrowIfProblemAsync(response, Strings.Get("DvChangeFailed"), cancellationToken);
    }

    /// <summary>The shared calendars and address books the device can tick, by its <c>collections</c> rel (#2090).</summary>
    public async Task<IReadOnlyList<DeviceCollectionChoice>> CollectionsAsync(ProtocolDevice device, CancellationToken cancellationToken = default)
    {
        var href = device.Links?.Href("collections") ?? throw new InvalidOperationException("The device advertised no 'collections' rel (ADR 0543).");
        var json = await core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        return [.. json.GetProperty("items").EnumerateArray().Select(c => new DeviceCollectionChoice(
            c.GetProperty("id").GetGuid(), c.GetProperty("name").GetString() ?? string.Empty,
            c.GetProperty("kind").GetString() ?? "calendar", Flag(c, "ticked")))];
    }

    public async Task RevokeAsync(ProtocolDevice device, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, Self(device.Links));
        message.Headers.TryAddWithoutValidation("If-Match", device.ETag);
        using var response = await core.Http.SendAsync(message, cancellationToken);
        await ApiWire.ThrowIfProblemAsync(response, Strings.Get("DvRevokeFailed"), cancellationToken);
    }

    public async Task SetShowAllDocumentsAsync(DeviceList list, bool showAllDocuments, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.PutAsJsonAsync(
            list.Links?.Href("settings") ?? throw new InvalidOperationException("The devices resource advertised no 'settings' rel (ADR 0543)."),
            new { showAllDocuments }, cancellationToken);
        await ApiWire.ThrowIfProblemAsync(response, Strings.Get("DvChangeFailed"), cancellationToken);
    }

    private static string Self(LinkMap? links) =>
        links?.Href("self") ?? throw new InvalidOperationException("The device resource advertised no 'self' rel (ADR 0543).");

    private static object Body(DeviceRequest request) => new
    {
        name = request.Name,
        mail = request.Mail,
        files = request.Files,
        calendars = request.Calendars,
        contacts = request.Contacts,
        encryptMail = request.EncryptMail,
        collectionsChosen = request.CollectionsChosen,
        chosenCollections = request.ChosenCollections,
    };

    private static DeviceList ParseList(JsonElement json) => new(
        [.. json.GetProperty("items").EnumerateArray().Select(ParseDevice)],
        Flag(json, "encryptMailLocked"),
        Flag(json, "showAllDocuments"),
        ParseServers(json.GetProperty("servers")),
        ApiCore.ParseLinks(json));

    private static ProtocolDevice ParseDevice(JsonElement json) => new(
        json.GetProperty("name").GetString() ?? string.Empty,
        json.GetProperty("kind").GetString() ?? "enrolled",
        Flag(json, "mail"), Flag(json, "files"), Flag(json, "calendars"), Flag(json, "contacts"),
        Flag(json, "encryptMail"), Flag(json, "encryptMailLocked"),
        json.GetProperty("createdAt").GetDateTimeOffset(),
        json.TryGetProperty("lastUsedAt", out var used) && used.ValueKind == JsonValueKind.String ? used.GetDateTimeOffset() : null,
        Flag(json, "canEdit"), Flag(json, "canDelete"),
        json.GetProperty("eTag").GetString() ?? string.Empty,
        ApiCore.ParseLinks(json),
        Flag(json, "collectionsChosen"),
        json.TryGetProperty("chosenCollections", out var chosen) && chosen.ValueKind == JsonValueKind.Array
            ? [.. chosen.EnumerateArray().Select(c => c.GetGuid())]
            : []);

    private static DeviceServers ParseServers(JsonElement json) => new(
        json.GetProperty("username").GetString() ?? string.Empty,
        Flag(json, "imapAvailable"),
        ApiWire.StrOrNull(json, "imapHost"),
        Int(json, "imapPort"),
        Int(json, "imapTlsPort"),
        json.GetProperty("outgoingHost").GetString() ?? string.Empty,
        json.GetProperty("webDavUrl").GetString() ?? string.Empty,
        json.GetProperty("calDavUrl").GetString() ?? string.Empty,
        json.GetProperty("cardDavUrl").GetString() ?? string.Empty);

    private static bool Flag(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
}
