using System.Security.Cryptography;
using System.Text.Json;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.BrowserHost;

namespace Alcifra.BrowserExtensionTests;

/// <summary>One entry the fake store holds; the secret is a test value. <paramref name="CodeSetup"/> is a verification
/// code's setup (ADR 1013), or null.</summary>
public sealed record FakeEntry(Guid Id, string Site, string Name, string Path, string? UserName, string Password, string? CodeSetup = null);

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
            .Select(e => new AutofillMatch(e.Id, e.Name, e.Path, e.UserName, e.CodeSetup is not null))
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken)
    {
        Record(new FakeCall(hostName, "open", null, entryId, null, null));
        var entry = Entries().SingleOrDefault(e => e.Id == entryId);
        if (entry is null)
        {
            return Task.FromResult<AutofillCredentials?>(null);
        }

        // Shaped by the real rule both hosts use, so the code the page fills is the one they would hand it.
        var content = new PasswordEntryContent(entry.Password,
        [
            .. entry.UserName is null ? [] : new[] { new PasswordEntryField(PasswordEntryFieldNames.UserName, entry.UserName) },
            .. entry.CodeSetup is null ? [] : new[] { new PasswordEntryField(PasswordEntryFieldNames.VerificationCode, entry.CodeSetup, IsSecret: true) },
        ]);
        return Task.FromResult<AutofillCredentials?>(PasswordStoreAutofill.Credentials(content, TimeProvider.System));
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
