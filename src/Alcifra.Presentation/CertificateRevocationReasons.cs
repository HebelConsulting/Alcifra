namespace Alcifra.Presentation;

/// <summary>
/// Why a key was revoked (#2278, ADR 1083): the reasons every client offers, in the order they offer them, and how a stored
/// reason reads back. The server keeps the code, or the words a person wrote for "Other"; the clients own the words for the
/// codes (the keys), so the web, the desktop, the phone and <c>alcifra</c> cannot disagree about what may be given.
/// </summary>
public static class CertificateRevocationReasons
{
    /// <summary>The reasons with a fixed meaning, as stored.</summary>
    public static readonly IReadOnlyList<string> Codes = ["lost", "compromised", "replaced", "left"];

    /// <summary>The reason that carries a person's own words instead of a code.</summary>
    public const string Other = "other";

    /// <summary>How long those words may be.</summary>
    public const int OtherTextLimit = 200;

    /// <summary>Every choice a client offers, "Other" last.</summary>
    public static IReadOnlyList<string> Choices { get; } = [.. Codes, Other];

    /// <summary>The localization key naming a choice in a list: "lost" → <c>RevokeReasonLost</c>.</summary>
    public static string ChoiceKey(string choice) => $"RevokeReason{char.ToUpperInvariant(choice[0])}{choice[1..]}";

    /// <summary>
    /// How a STORED reason reads: a code's key to translate, or null when it is a person's own words, shown as written.
    /// </summary>
    public static string? StoredKey(string? stored) =>
        stored is not null && Codes.Contains(stored) ? ChoiceKey(stored) : null;

    /// <summary>What is stored for a choice: its code, or the trimmed words for "Other"; null when the choice is incomplete.</summary>
    public static string? Stored(string? choice, string? otherText) => choice switch
    {
        _ when choice is not null && Codes.Contains(choice) => choice,
        Other when otherText?.Trim() is { Length: > 0 and <= OtherTextLimit } text => text,
        _ => null,
    };
}
