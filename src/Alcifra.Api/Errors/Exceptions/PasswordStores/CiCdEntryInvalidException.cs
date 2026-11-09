using Alcifra.Presentation;
using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// A CI/CD secret's key names, variables or targets break the shared rules (ADR 1084, CiCdEntryRules): refused before
// anything is written, so the index never names a key the platform would refuse.
public sealed class CiCdEntryInvalidException(IReadOnlyList<CiCdEntryProblem> problems) : PasswordStoreException(
    "CICD_ENTRY_INVALID", StatusCodes.Status422UnprocessableEntity,
    $"The CI/CD secret's index data is not valid: {string.Join(", ", problems)}.")
{
    /// <summary>What was wrong, in the order a person would fix it.</summary>
    public IReadOnlyList<CiCdEntryProblem> Problems { get; } = problems;
}
