namespace Alcifra.Presentation;

/// <summary>
/// How a protocol device is described, which every client answers identically (#1970 slice 3; ADRs 0650/0651):
/// arithmetic, not formatting. The answers are text KEYS; each client looks them up in its own language and draws them.
/// </summary>
public static class ProtocolDeviceText
{
    /// <summary>The name an earlier password is shown under (the stored name is English), or null to show the device's own.</summary>
    public static string? KindNameKey(string kind) => kind switch
    {
        "earlierDav" => "DvEarlierDav",
        "earlierImap" => "DvEarlierImap",
        _ => null,
    };

    /// <summary>What a device reaches, in the order the switches are shown; "nothing" when every switch is off.</summary>
    public static IReadOnlyList<string> ReachesKeys(bool mail, bool files, bool calendars, bool contacts)
    {
        var keys = new List<string>();
        if (mail)
        {
            keys.Add("DvMail");
        }

        if (files)
        {
            keys.Add("DvFiles");
        }

        if (calendars)
        {
            keys.Add("DvCalendars");
        }

        if (contacts)
        {
            keys.Add("DvContacts");
        }

        return keys.Count == 0 ? ["DvNoProtocols"] : keys;
    }

    /// <summary>A device's mail state: locked on by the organisation, encrypted, or not.</summary>
    public static string MailStateKey(bool locked, bool encrypted) =>
        locked ? "DvEncryptLocked" : encrypted ? "DvEncrypted" : "DvNotEncrypted";
}
