using Alcifra.ApiClient.PasswordStore;
using Alcifra.Presentation;

namespace Alcifra.Pass.Infrastructure;

/// <summary>Where a key stands on a target (ADR 1084).</summary>
public enum CiCdKeyState
{
    /// <summary>The target lacks it.</summary>
    Missing,

    /// <summary>A secret the entry changed after the target's update time, or a variable whose value differs.</summary>
    Stale,

    /// <summary>Nothing to do.</summary>
    Unchanged,
}

/// <summary>What a target holds, as the platform shows it: secret names with their update times, variables with their values.</summary>
public sealed record CiCdTargetState(IReadOnlyDictionary<string, DateTimeOffset> Secrets, IReadOnlyDictionary<string, string> Variables);

/// <summary>One key of one entry on one target, and where it stands there.</summary>
public sealed record CiCdStep(string Entry, string Key, string Target, bool IsVariable, CiCdKeyState State);

/// <summary>
/// What <c>apass cicd apply</c> would do (ADR 1084), from the entries' PLAIN index and what each target shows: no key, no
/// value. So a dry run needs no PIN, and the decision is testable without a platform or a card.
/// </summary>
public static class CiCdPlan
{
    /// <summary>Every key of every entry on each of its targets, in entry, target and key order.</summary>
    /// <param name="onlyTarget">One target to plan for; null for all of them.</param>
    public static IReadOnlyList<CiCdStep> For(
        IReadOnlyList<(string Entry, CiCdIndexInfo Index)> entries, IReadOnlyDictionary<string, CiCdTargetState> targets, string? onlyTarget = null)
    {
        var steps = new List<CiCdStep>();
        foreach (var (entry, index) in entries)
        {
            foreach (var target in index.Targets.Where(t => onlyTarget is null || string.Equals(t, onlyTarget, StringComparison.Ordinal)))
            {
                var state = targets.GetValueOrDefault(target) ?? new CiCdTargetState(new Dictionary<string, DateTimeOffset>(), new Dictionary<string, string>());
                foreach (var key in index.SecretKeys)
                {
                    steps.Add(new CiCdStep(entry, key, target, IsVariable: false, SecretState(key, index.ChangedAt, state)));
                }

                foreach (var variable in index.Variables.Select(CiCdEntryRules.SplitVariable).OfType<(string Name, string Value)>())
                {
                    steps.Add(new CiCdStep(entry, variable.Name, target, IsVariable: true, VariableState(variable, state)));
                }
            }
        }

        return steps;
    }

    // The platform names keys without regard to case, as it stores them upper-cased.
    private static CiCdKeyState SecretState(string key, DateTimeOffset? changedAt, CiCdTargetState state) =>
        state.Secrets.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } held
            ? changedAt is { } changed && held.Value < changed ? CiCdKeyState.Stale : CiCdKeyState.Unchanged
            : CiCdKeyState.Missing;

    private static CiCdKeyState VariableState((string Name, string Value) variable, CiCdTargetState state) =>
        state.Variables.FirstOrDefault(v => string.Equals(v.Key, variable.Name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } held
            ? string.Equals(held.Value, variable.Value, StringComparison.Ordinal) ? CiCdKeyState.Unchanged : CiCdKeyState.Stale
            : CiCdKeyState.Missing;
}
