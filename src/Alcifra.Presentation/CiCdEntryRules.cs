using System.Text.RegularExpressions;

namespace Alcifra.Presentation;

/// <summary>What is wrong with a CI/CD entry's plain index data (ADR 1084), in the order a person would fix it.</summary>
public enum CiCdEntryProblem
{
    /// <summary>A key name the platform would refuse: letters, digits and underscore, not starting with a digit or <c>GITHUB_</c>.</summary>
    InvalidKeyName,

    /// <summary>The same key name twice in one entry, across its secrets and variables, ignoring case as the platform does.</summary>
    DuplicateKeyName,

    /// <summary>A file key that is not one of the secret keys.</summary>
    FileKeyNotASecret,

    /// <summary>A variable written without <c>NAME=value</c>.</summary>
    InvalidVariable,

    /// <summary>A target that is not <c>github:owner/name</c> or <c>github:owner/name@environment</c>.</summary>
    InvalidTarget,
}

/// <summary>
/// The rules a CI/CD entry's key names, variables and targets follow (ADR 1084), answered identically by the server, the
/// desktop and <c>apass</c>, so none of them accepts what another refuses.
/// </summary>
public static partial class CiCdEntryRules
{
    /// <summary>The one platform a target may name today.</summary>
    public const string GitHub = "github";

    /// <summary>Every problem with the entry, each once, in the order of <see cref="CiCdEntryProblem"/>; empty when it is fine.</summary>
    public static IReadOnlyList<CiCdEntryProblem> Problems(
        IReadOnlyList<string> secretKeys, IReadOnlyList<string> fileKeys, IReadOnlyList<string> variables, IReadOnlyList<string> targets)
    {
        var variableNames = variables.Select(v => SplitVariable(v)?.Name).ToList();
        var names = secretKeys.Concat(variableNames.Where(n => n is not null).Select(n => n!)).ToList();
        var problems = new SortedSet<CiCdEntryProblem>();
        if (names.Any(n => !IsKeyName(n)))
        {
            problems.Add(CiCdEntryProblem.InvalidKeyName);
        }

        if (names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            problems.Add(CiCdEntryProblem.DuplicateKeyName);
        }

        if (fileKeys.Any(f => !secretKeys.Contains(f, StringComparer.Ordinal)))
        {
            problems.Add(CiCdEntryProblem.FileKeyNotASecret);
        }

        if (variableNames.Any(n => n is null))
        {
            problems.Add(CiCdEntryProblem.InvalidVariable);
        }

        if (targets.Any(t => ParseTarget(t) is null))
        {
            problems.Add(CiCdEntryProblem.InvalidTarget);
        }

        return [.. problems];
    }

    /// <summary>Whether <paramref name="name"/> is a key name the platform accepts.</summary>
    public static bool IsKeyName(string name) =>
        KeyName().IsMatch(name) && !name.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase);

    /// <summary>A variable's name and value, or null when it is not written as <c>NAME=value</c>.</summary>
    public static (string Name, string Value)? SplitVariable(string variable) =>
        variable.IndexOf('=') is var at and > 0 ? (variable[..at], variable[(at + 1)..]) : null;

    /// <summary>A target's repository (<c>owner/name</c>) and environment, or null when it is not a target.</summary>
    public static (string Repository, string? Environment)? ParseTarget(string target) =>
        Target().Match(target) is { Success: true } match
            ? (match.Groups["repo"].Value, match.Groups["env"].Success ? match.Groups["env"].Value : null)
            : null;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex KeyName();

    [GeneratedRegex("^github:(?<repo>[A-Za-z0-9-]+/[A-Za-z0-9._-]+)(@(?<env>[A-Za-z0-9._ -]+))?$")]
    private static partial Regex Target();
}
