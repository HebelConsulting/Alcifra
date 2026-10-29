using Microsoft.Extensions.Logging;
using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>
/// The hidden <c>__complete</c> verb the shell scripts call: the words typed so far in, one candidate per line out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Completion reads NAMES only, which is why it needs no key and no PIN</b> (ADR 0964): the store's tree is in
/// the clear, so a TAB is a listing, not a decryption.
/// </para>
/// <para>
/// <b>It never fails loudly and never hangs.</b> A broken session, an unreachable installation or a slow answer
/// prints nothing and exits 0 — a completion that writes an error into the user's prompt, or freezes the shell for
/// seconds, is worse than no candidate at all. The tree comes from the same short cache the commands use.
/// </para>
/// </remarks>
internal static class Completion
{
    private static readonly string[] Commands = ["login", "ls", "find", "show", "insert", "generate", "edit", "rm", "reseal", "completion"];

    /// <summary>Prints candidates for <paramref name="words"/> (the argv after the program name).</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            var last = words.Count > 0 ? words[^1] : string.Empty;
            var previous = words.Count > 1 ? words[^2] : string.Empty;
            var command = words.Count > 0 ? words[0] : string.Empty;

            // The store's NAME after --store/-s, from the installation (a name, not a path).
            if (last is "--store" or "-s" || previous is "--store" or "-s")
            {
                await NamesAsync(StoreNamesAsync(timeout.Token), previous is "--store" or "-s" ? last : string.Empty);
                return 0;
            }

            if (words.Count <= 1)
            {
                // The first word: the commands themselves, plus the options every command takes.
                if (last.StartsWith('-'))
                {
                    Print(["--store", "--refresh", "--verbose", "--help"]);
                    return 0;
                }

                Print(Commands.Where(c => c.StartsWith(last, StringComparison.Ordinal)));
                return 0;
            }

            if (last.StartsWith('-'))
            {
                Print(OptionsFor(command));
                return 0;
            }

            if (command == "completion")
            {
                Print(new[] { "bash", "zsh", "fish" }.Where(s => s.StartsWith(last, StringComparison.Ordinal)));
                return 0;
            }

            if (command == "login")
            {
                return 0;
            }

            // A path, for the commands that take one: the cached tree first, and only then the installation.
            var storeName = OptionValue(words, "--store", "-s");
            var partial = PasswordTree.Normalise(last).Contains('/') || last.Length == 0 ? PasswordTree.Normalise(last) : last;
            await NamesAsync(PathNamesAsync(storeName, partial, timeout.Token), partial);
            return 0;
        }
        catch (Exception e)
        {
            // Never into the user's prompt: completion is an aid, and a failed one is silence.
            SapassLog.For(nameof(Completion)).LogDebug("Completion produced no candidates: {Reason}", e.Message);
            return 0;
        }
    }

    private static async Task<IReadOnlyList<string>> StoreNamesAsync(CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment();
        var (stores, offersMyPasswords) = await session.Passwords.ListStoresAsync(cancellationToken);
        return StoreCatalog.CompletionNames(stores, offersMyPasswords);
    }

    private static async Task<IReadOnlyList<string>> PathNamesAsync(string? storeName, string partial, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment();
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(storeName, cancellationToken);
        var rows = await catalog.RowsAsync(store, refresh: false, cancellationToken);

        // The partial's own folder, so a TAB after "Mail/" offers Mail's children rather than the whole tree's.
        var slash = partial.LastIndexOf('/');
        var folder = slash >= 0 ? partial[..slash] : string.Empty;
        var leaf = slash >= 0 ? partial[(slash + 1)..] : partial;

        return
        [
            .. PasswordTree.Children(rows, folder)
                .Where(r => r.Name.StartsWith(leaf, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.IsFolder ? $"{folder}/{r.Name}/".TrimStart('/') : $"{folder}/{r.Name}".TrimStart('/')),
        ];
    }

    private static string[] OptionsFor(string command) => command switch
    {
        "login" => ["--url", "--verbose", "--help"],
        "insert" => ["--store", "--refresh", "--user", "--entry-url", "--verbose", "--help"],
        "generate" => ["--store", "--refresh", "--length", "--user", "--entry-url", "--verbose", "--help"],
        _ => ["--store", "--refresh", "--verbose", "--help"],
    };

    private static string? OptionValue(IReadOnlyList<string> words, params string[] names)
    {
        for (var i = 0; i < words.Count - 1; i++)
        {
            if (names.Contains(words[i], StringComparer.Ordinal))
            {
                return words[i + 1];
            }
        }

        return null;
    }

    private static async Task NamesAsync(Task<IReadOnlyList<string>> names, string prefix)
    {
        Print((await names).Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static void Print(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            Console.Out.WriteLine(candidate);
        }
    }
}
