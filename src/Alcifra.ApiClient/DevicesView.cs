using System.Globalization;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.ApiClient;

/// <summary>One device as the Devices view shows it, in the user's language (#1970).</summary>
/// <param name="Name">The device's name; an earlier password's is the localized one, not the stored English.</param>
/// <param name="Reaches">What it reaches, e.g. "Mail, Calendars".</param>
/// <param name="MailState">"encrypted", "not encrypted", or "Required by your organisation".</param>
/// <param name="LastUsed">When a login last used it, or "never".</param>
public sealed record DeviceRow(
    ProtocolDevice Device, string Name, string Reaches, string MailState, string LastUsed, bool CanEdit, bool CanDelete);

/// <summary>
/// The Devices view, UI-free and shared by the desktop, the web and (later) the phone (#1970 slice 3; ADR 0917): it holds
/// the user's device list, shapes what each client draws, and performs the four acts. A client binds to it and draws;
/// it never decides what a row says.
/// </summary>
public sealed class DevicesView(DevicesClient devices)
{
    /// <summary>The list as last read; null until <see cref="LoadAsync"/>.</summary>
    public DeviceList? List { get; private set; }

    public IReadOnlyList<DeviceRow> Rows => List is null ? [] : RowsOf(List, CultureInfo.CurrentCulture);

    public IReadOnlyList<DeviceServerLine> ServerLines => List is null ? [] : ServerLinesOf(List.Servers);

    /// <summary>The device just enrolled, with its password and hand-over, until the person closes the hand-over.</summary>
    public EnrolledDevice? HandOver { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default) => List = await devices.GetAsync(cancellationToken);

    /// <summary>Enrols a device and keeps its hand-over to show; the list is read again.</summary>
    public async Task<EnrolledDevice> EnrolAsync(DeviceRequest request, CancellationToken cancellationToken = default)
    {
        var list = List ?? await devices.GetAsync(cancellationToken);
        HandOver = await devices.EnrolAsync(list, request, cancellationToken);
        await LoadAsync(cancellationToken);
        return HandOver;
    }

    public async Task ChangeAsync(ProtocolDevice device, DeviceRequest request, CancellationToken cancellationToken = default)
    {
        await devices.UpdateAsync(device, request, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task RevokeAsync(ProtocolDevice device, CancellationToken cancellationToken = default)
    {
        await devices.RevokeAsync(device, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task SetShowAllDocumentsAsync(bool showAllDocuments, CancellationToken cancellationToken = default)
    {
        var list = List ?? await devices.GetAsync(cancellationToken);
        await devices.SetShowAllDocumentsAsync(list, showAllDocuments, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>Forgets the hand-over: its password is shown once.</summary>
    public void CloseHandOver() => HandOver = null;

    /// <summary>A device's request as it stands, the base of a change (every field stated, ADR 0872).</summary>
    public static DeviceRequest RequestOf(ProtocolDevice device) =>
        new(device.Name, device.Mail, device.Files, device.Calendars, device.Contacts, device.EncryptMail && !device.EncryptMailLocked);

    /// <summary>The rows, in the list's order.</summary>
    public static IReadOnlyList<DeviceRow> RowsOf(DeviceList list, CultureInfo culture) =>
    [
        .. list.Items.Select(d => new DeviceRow(
            d,
            ProtocolDeviceText.KindNameKey(d.Kind) is { } nameKey ? Strings.Get(nameKey) : d.Name,
            string.Join(", ", ProtocolDeviceText.ReachesKeys(d.Mail, d.Files, d.Calendars, d.Contacts).Select(Strings.Get)),
            Strings.Get(ProtocolDeviceText.MailStateKey(d.EncryptMailLocked, d.EncryptMail)),
            d.LastUsedAt is { } used ? used.InZone(SessionTimeZone.Current).ToString("g", culture) : Strings.Get("DvNever"),
            d.CanEdit,
            d.CanDelete)),
    ];

    /// <summary>What a device is set up with, line by line, as the Devices view and the hand-over show it.</summary>
    public static IReadOnlyList<DeviceServerLine> ServerLinesOf(DeviceServers s) => DeviceServerLines.Of(
        s.Username, s.ImapAvailable, s.ImapHost, s.ImapPort, s.ImapTlsPort, s.OutgoingHost, s.WebDavUrl, s.CalDavUrl, s.CardDavUrl, Strings.Get);
}
