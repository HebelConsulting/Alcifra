namespace Alcifra.Api.Errors.Exceptions.Documents;

/// <summary>The arriving content is byte-identical to documents already in the archive, and the caller asked for the
/// confirmation without bringing the user's explicit "file anyway" (core #2476). The candidates ride the Problem Details
/// as data (the same extension-member pattern as #703), so a script reads what the interactive clients already showed —
/// never by parsing the sentence.
/// </summary>
public sealed class DuplicateContentException(IReadOnlyList<(Guid Id, string Name)> candidates)
    : ApiException(
        "DUPLICATE_CONTENT",
        409,
        $"The content duplicates existing documents: {string.Join("; ", candidates.Select(c => $"'{c.Name}'"))}. " +
        "File it anyway by sending the duplicate decision as allow.",
        new Dictionary<string, object?>
        {
            ["duplicates"] = candidates.Select(c => new { id = c.Id, name = c.Name }).ToList(),
        });
