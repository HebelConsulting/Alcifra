namespace Alcifra.Domain.Masks;

/// <summary>
/// A mask names the file types it holds and this content is not one (ABI 1.29, ADR 1046). Thrown from <c>SaveChanges</c>,
/// the one door every filing path uses, and translated at the Api boundary like the other mask invariants.
/// </summary>
public sealed class ContentExtensionNotAcceptedException : InvalidOperationException
{
    public ContentExtensionNotAcceptedException(string maskName, string extension, IEnumerable<string> accepted)
        : base($"A {maskName} holds {string.Join(", ", accepted.Order(StringComparer.Ordinal).Select(e => $".{e}"))}; "
            + $"{(extension.Length == 0 ? "a file without an extension" : $"'{extension}'")} is not one of them.")
    {
    }
}
