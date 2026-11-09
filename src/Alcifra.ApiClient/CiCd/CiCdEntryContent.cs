using System.Text.Json;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Presentation;

namespace Alcifra.ApiClient.CiCd;

/// <summary>One secret value of a CI/CD entry (ADR 1084): its name, its raw bytes, and whether it is a file sent base64-encoded.</summary>
public sealed record CiCdSecretValue(string Name, byte[] Value, bool IsFile);

/// <summary>One variable of a CI/CD entry: not secret, so it lives in the plain index data.</summary>
public sealed record CiCdVariable(string Name, string Value);

/// <summary>
/// A CI/CD entry as a person edits it (ADR 1084): where it is set, its secret values and its variables. The secret values
/// are sealed as one payload; everything else is the entry's plain index data.
/// </summary>
public sealed record CiCdEntryContent(IReadOnlyList<string> Targets, IReadOnlyList<CiCdSecretValue> Secrets, IReadOnlyList<CiCdVariable> Variables)
{
    /// <summary>What the shared rules refuse in it (<see cref="CiCdEntryRules"/>); empty when it may be saved.</summary>
    public IReadOnlyList<CiCdEntryProblem> Problems =>
        CiCdEntryRules.Problems(
            [.. Secrets.Select(s => s.Name)], [.. Secrets.Where(s => s.IsFile).Select(s => s.Name)],
            [.. Variables.Select(v => $"{v.Name}={v.Value}")], Targets);

    /// <summary>The values as JSON, <c>{ "values": { NAME: base64 } }</c>: what is compared to tell a changed value.</summary>
    public byte[] ToJson() =>
        JsonSerializer.SerializeToUtf8Bytes(new { values = Secrets.ToDictionary(s => s.Name, s => Convert.ToBase64String(s.Value)) });

    /// <summary>
    /// The sealed payload: the JSON as one MIME part, as every sealed entry's payload is a MIME entity (ADR 0964), so each
    /// host's opener hands it back unchanged.
    /// </summary>
    public byte[] ToPayload() => SmimeEnvelope.Part(ToJson(), "application/json");

    /// <summary>The plain index data this content states, as the version confirm carries it.</summary>
    public IReadOnlyDictionary<string, object?> IndexParts() => new Dictionary<string, object?>
    {
        ["ciCd"] = new
        {
            targets = Targets,
            secretKeys = Secrets.Select(s => s.Name).ToList(),
            fileKeys = Secrets.Where(s => s.IsFile).Select(s => s.Name).ToList(),
            variables = Variables.Select(v => $"{v.Name}={v.Value}").ToList(),
        },
    };

    /// <summary>The content of an opened entry: its plain index and the JSON its payload opened to.</summary>
    /// <exception cref="CiCdEntryUnreadableException">The payload is not a CI/CD entry's.</exception>
    public static CiCdEntryContent From(CiCdIndexInfo index, byte[] payload)
    {
        Dictionary<string, string> values;
        try
        {
            using var json = JsonDocument.Parse(payload);
            values = json.RootElement.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CiCdEntryUnreadableException(e);
        }

        return new CiCdEntryContent(
            index.Targets,
            [.. index.SecretKeys.Select(name => new CiCdSecretValue(
                name, values.TryGetValue(name, out var value) ? Convert.FromBase64String(value) : [], index.FileKeys.Contains(name)))],
            [.. index.Variables.Select(CiCdEntryRules.SplitVariable).Where(v => v is not null).Select(v => new CiCdVariable(v!.Value.Name, v.Value.Value))]);
    }
}

/// <summary>An opened payload that is not a CI/CD entry's: sealed by something else, or damaged.</summary>
public sealed class CiCdEntryUnreadableException(Exception inner)
    : Exception("The secret's sealed content is not a CI/CD entry's.", inner);
