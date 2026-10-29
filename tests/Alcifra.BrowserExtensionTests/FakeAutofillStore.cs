using System.Security.Cryptography;
using System.Text.Json;
using Alcifra.BrowserHost;

namespace Alcifra.BrowserExtensionTests;

/// <summary>One entry the fake store holds; the secret is a test value.</summary>
public sealed record FakeEntry(Guid Id, string Site, string Name, string Path, string? UserName, string Password);

/// <summary>One call the store answered, recorded for the test to assert on; <see cref="Host"/> names which
/// registered client served it.</summary>
public sealed record FakeCall(string Host, string Command, string? Site, Guid? EntryId, string? UserName, string? Password);

/// <summary>
/// A password store with no server and no card: entries come from <c>entries.json</c> in the state folder, and every
/// answer is appended to <c>calls.jsonl</c> there, because each browser request runs in its own process.
/// </summary>
public sealed class FakeAutofillStore(string stateFolder, string hostName) : IAutofillStore
{
    private const string EntriesFile = "entries.json";
    private const string CallsFile = "calls.jsonl";

    /// <summary>Seeds the entries a test's store holds.</summary>
    public static void Seed(string stateFolder, IEnumerable<FakeEntry> entries)
    {
        Directory.CreateDirectory(stateFolder);
        File.WriteAllText(Path.Combine(stateFolder, EntriesFile), JsonSerializer.Serialize(entries.ToList()));
    }

    /// <summary>What the store answered so far, in order.</summary>
    public static IReadOnlyList<FakeCall> Calls(string stateFolder)
    {
        var path = Path.Combine(stateFolder, CallsFile);
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<FakeCall>(l)!).ToList()
            : [];
    }

    public Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken)
    {
        Record(new FakeCall(hostName, "match", site, null, null, null));
        IReadOnlyList<AutofillMatch> matches = Entries()
            .Where(e => string.Equals(e.Site, site, StringComparison.OrdinalIgnoreCase))
            .Select(e => new AutofillMatch(e.Id, e.Name, e.Path, e.UserName))
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken)
    {
        Record(new FakeCall(hostName, "open", null, entryId, null, null));
        var entry = Entries().SingleOrDefault(e => e.Id == entryId);
        return Task.FromResult(entry is null ? null : new AutofillCredentials(entry.Password, entry.UserName, []));
    }

    public Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken)
    {
        var id = save.EntryId ?? Guid.NewGuid();
        Record(new FakeCall(hostName, "save", save.Site, id, save.UserName, save.Password));
        return Task.FromResult(new AutofillSaved(id, save.Site, "My Passwords"));
    }

    public string Generate(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_!";
        var password = RandomNumberGenerator.GetString(alphabet, length);
        Record(new FakeCall(hostName, "generate", null, null, null, password));
        return password;
    }

    private List<FakeEntry> Entries()
    {
        var path = Path.Combine(stateFolder, EntriesFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<FakeEntry>>(File.ReadAllText(path)) ?? [] : [];
    }

    private void Record(FakeCall call) =>
        File.AppendAllText(Path.Combine(stateFolder, CallsFile), $"{JsonSerializer.Serialize(call)}\n");
}
