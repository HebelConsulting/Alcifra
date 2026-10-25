using SimplArchive.ApiClient.PasswordStore;

namespace SimplArchive.Sapass.Infrastructure;

/// <summary>What a password command computes without the wire: the secret itself, and where the path splits.</summary>
internal static class Passwords
{
    public const int DefaultLength = 24;

    /// <summary>
    /// A new password from the SHARED generator (ADR 0964) — the phone's and the desktop's own — with ambiguous
    /// characters left out: the result is often read off a screen and typed where nothing can be pasted.
    /// </summary>
    public static string Generate(int length) =>
        PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: length, AvoidAmbiguous: true));

    /// <summary>An entry path split into its folder path and its name: <c>Mail/Banking/login</c> → <c>Mail/Banking</c>, <c>login</c>.</summary>
    public static (string Folder, string Name) Split(string path)
    {
        var normalised = PasswordTree.Normalise(path);
        var slash = normalised.LastIndexOf('/');
        return slash < 0 ? (string.Empty, normalised) : (normalised[..slash], normalised[(slash + 1)..]);
    }
}
