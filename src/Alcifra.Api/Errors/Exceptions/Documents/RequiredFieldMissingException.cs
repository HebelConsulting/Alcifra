using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

/// <summary>
/// A document's mask requires a field it has no value for (ADR 0176). The problem details name the field as DATA
/// (<c>field</c>), so a client says "{field} is required" in the reader's language instead of quoting the server's
/// English sentence (#2217, Mobile #271, issue #424's rule).
/// </summary>
public sealed partial class RequiredFieldMissingException : DocumentException
{
    public RequiredFieldMissingException(string message)
        : base("REQUIRED_FIELD_MISSING", StatusCodes.Status400BadRequest, message, FieldOf(message))
    {
    }

    /// <summary>The field the invariant named, or null for a message that names none.</summary>
    public string? FieldName => Extensions?["field"] as string;

    // The invariant's sentence ends "… required field 'Name'." (AlcifraDbContext.ValidateRequiredFieldsAsync); the name
    // is read out of it here, once, rather than every endpoint parsing the prose it then throws away.
    private static IReadOnlyDictionary<string, object?>? FieldOf(string message) =>
        FieldInMessage().Match(message) is { Success: true } match
            ? new Dictionary<string, object?> { ["field"] = match.Groups["field"].Value }
            : null;

    [GeneratedRegex(@"required field '(?<field>[^']+)'")]
    private static partial Regex FieldInMessage();
}
