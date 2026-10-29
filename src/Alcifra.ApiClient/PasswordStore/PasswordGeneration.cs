using System.Globalization;
using Alcifra.Localization;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>A generated password, or why none could be: the sentence for the user.</summary>
public sealed record GeneratedPassword(string? Password, string? Problem);

/// <summary>
/// The generator's form (Mobile ADR 0037; both native clients since ADR 0968) over the core's <see cref="PasswordGenerator"/>: the length is TYPED (ADR 0874),
/// the classes are switches, and a length the chosen classes cannot fill is said in the user's language with the range
/// that works, rather than as the generator's English refusal.
/// </summary>
public static class PasswordGeneration
{
    public static GeneratedPassword Generate(string lengthText, PasswordGeneratorOptions classes)
    {
        var count = PasswordGenerator.Classes(classes).Count;
        if (count == 0)
        {
            return new GeneratedPassword(null, Strings.Get("PwGenerateNoClass"));
        }

        if (!int.TryParse(lengthText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var length)
            || length < count || length > PasswordGenerator.MaximumLength)
        {
            return new GeneratedPassword(null, string.Format(CultureInfo.CurrentCulture, Strings.Get("PwGenerateLengthRange"), count, PasswordGenerator.MaximumLength));
        }

        return new GeneratedPassword(PasswordGenerator.Generate(classes with { Length = length }), null);
    }
}
