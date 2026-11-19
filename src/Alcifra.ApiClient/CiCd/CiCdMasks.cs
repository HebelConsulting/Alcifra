namespace Alcifra.ApiClient.CiCd;

/// <summary>
/// The masks the CI/CD store family wears (ADR 1084), named once for every client that recognises them: a store is a
/// flat folder holding only secrets, and a client that met one in a tree has to tell it from an ordinary folder without a
/// request.
/// </summary>
/// <remarks>
/// A mask NAME, as the desktop's own constant was, deliberately not the icon token: the token is that client's rendering
/// concern, and two clients comparing different things is how one of them stops recognising the family.
/// </remarks>
public static class CiCdMasks
{
    /// <summary>The secret's mask.</summary>
    public const string SecretMaskName = "CI/CD Secret";

    /// <summary>The store's own mask: the folder whose secrets are made through its own door.</summary>
    public const string StoreMaskName = "CI/CD Store";

    /// <summary>Whether a document row's mask name is a CI/CD secret's.</summary>
    public static bool IsSecret(string? maskName) => string.Equals(maskName, SecretMaskName, StringComparison.Ordinal);

    /// <summary>Whether a folder row's mask name is a CI/CD store's.</summary>
    public static bool IsStore(string? maskName) => string.Equals(maskName, StoreMaskName, StringComparison.Ordinal);
}
