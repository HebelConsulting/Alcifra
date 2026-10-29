using System.Security.Cryptography;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>What a generated password is made of (ADR 0964). The defaults suit nearly every site.</summary>
/// <param name="Length">How many characters, at least one per class chosen.</param>
/// <param name="AvoidAmbiguous">Leave out characters easily misread when typed from a screen (0/O, 1/l/I, |).</param>
public sealed record PasswordGeneratorOptions(
    int Length = 20,
    bool Lowercase = true,
    bool Uppercase = true,
    bool Digits = true,
    bool Symbols = true,
    bool AvoidAmbiguous = false);

/// <summary>
/// Generates passwords from the platform's cryptographic random source (ADR 0964): every character drawn uniformly
/// from the chosen classes, at least one from each, positions shuffled, never a modulo bias.
/// </summary>
public static class PasswordGenerator
{
    public const int MaximumLength = 1024;

    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digit = "0123456789";
    private const string Symbol = "!#$%&()*+,-./:;<=>?@[]^_{|}~";
    private const string Ambiguous = "0O1lI|";

    /// <summary>A new password.</summary>
    /// <exception cref="ArgumentException">No class chosen, or a length that cannot hold one of each or exceeds the maximum.</exception>
    public static string Generate(PasswordGeneratorOptions? options = null)
    {
        options ??= new PasswordGeneratorOptions();
        var classes = Classes(options);
        if (classes.Count == 0)
        {
            throw new ArgumentException("A password needs at least one class of characters.", nameof(options));
        }

        if (options.Length < classes.Count || options.Length > MaximumLength)
        {
            throw new ArgumentException($"A password of these classes is between {classes.Count} and {MaximumLength} characters long.", nameof(options));
        }

        var all = string.Concat(classes);
        var characters = new char[options.Length];
        for (var i = 0; i < characters.Length; i++)
        {
            // The first positions take one of each class, the rest any; the shuffle then hides which were which.
            var from = i < classes.Count ? classes[i] : all;
            characters[i] = from[RandomNumberGenerator.GetInt32(from.Length)];
        }

        for (var i = characters.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }

        return new string(characters);
    }

    /// <summary>The character classes the options choose, each as the characters it may draw.</summary>
    public static IReadOnlyList<string> Classes(PasswordGeneratorOptions options) =>
        [
            .. new[] { (options.Lowercase, Lower), (options.Uppercase, Upper), (options.Digits, Digit), (options.Symbols, Symbol) }
                .Where(c => c.Item1)
                .Select(c => options.AvoidAmbiguous ? string.Concat(c.Item2.Where(ch => !Ambiguous.Contains(ch))) : c.Item2),
        ];
}
