using System.Globalization;
using System.Net;
using SimplArchive.Localization;

namespace SimplArchive.ApiClient.PasswordStore;

/// <summary>An import read and checked against the folder, waiting for the user's answers to its conflicts.</summary>
/// <param name="Existing">The entries already in the folder, those with a clashing name opened for their user names.</param>
public sealed record PasswordImportPreview(PasswordImportResult Read, IReadOnlyList<PasswordImportConflict> Conflicts, IReadOnlyList<ExistingPasswordEntry> Existing);

/// <summary>
/// What the Passwords screens of BOTH native clients do (Mobile ADR 0037, moved here by ADR 0968), over the shared client
/// (ADR 0964), so neither client keeps its own copy: open an entry with the host's key, re-seal a store with one PIN, and
/// import an export with the user deciding each conflict. Sealing is always on the device.
/// </summary>
/// <param name="keys">The host's key opener for a password entry: the phone's key dialog, the desktop's hardware key.</param>
/// <param name="skipping">The same key, but an entry it cannot open is REPORTED rather than asked about again (the phone's
/// <c>SkippingUnopenable</c>); null where the host's opener already declines without asking twice (the desktop's card
/// session remembers its PIN and its decline).</param>
/// <param name="oneUpdate">Runs a series of reads inside ONE key session, so a re-seal or an import asks for the PIN once
/// (the phone's <c>OneUpdateAsync</c>); null where the host holds its key session anyway.</param>
public sealed class PasswordWork(ApiCore core, IEnvelopeOpener keys, IEnvelopeOpener? skipping = null, Func<Func<Task>, Task>? oneUpdate = null)
{
    private readonly PasswordStoreClient _skipping = new(core, skipping ?? keys);

    /// <summary>The shared client, opening with the host's key.</summary>
    public PasswordStoreClient Client { get; } = new(core, keys);

    public PasswordStoreList Stores => new(Client);

    /// <summary>An entry's row read and opened with the key: the resource first, its ETag what the next save sends.</summary>
    public async Task<OpenedPasswordEntry> OpenAsync(PasswordItemInfo row, CancellationToken cancellationToken = default) =>
        await Client.OpenAsync(await Client.GetEntryAsync(row.Href, cancellationToken), cancellationToken);

    /// <summary>
    /// Re-seals the store with ONE PIN (owner decision 2026-10-23): every listed entry opened with the same key, an entry
    /// this key cannot open reported rather than asked about again, one somebody changed meanwhile skipped (412).
    /// </summary>
    public async Task<PasswordResealOutcome> ResealAsync(PasswordFolderInfo store, CancellationToken cancellationToken = default)
    {
        PasswordResealOutcome? outcome = null;
        await InOneUpdateAsync(async () => outcome = await _skipping.ResealAsync(store, cancellationToken));
        return outcome!;
    }

    /// <summary>
    /// Reads an export (the CSV a password manager exports) on this device and finds its conflicts with the folder: only
    /// the entries whose NAME an imported row has are opened, with one PIN, to learn their user names; one this key does
    /// not open is taken to have none, so its row is imported beside it under a free name rather than over it.
    /// </summary>
    public async Task<PasswordImportPreview> PrepareImportAsync(PasswordFolderInfo folder, string csv, CancellationToken cancellationToken = default)
    {
        var read = PasswordImport.ReadGoogleCsv(csv);
        var names = read.Drafts.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = folder.Items.Where(i => !i.IsFolder).Select(i => new ExistingPasswordEntry(i.Id, i.Name, null, i.Href)).ToList();
        var clashing = existing.Select((e, at) => (e, at)).Where(x => names.Contains(x.e.Name)).ToList();
        if (clashing.Count > 0)
        {
            await InOneUpdateAsync(async () =>
            {
                foreach (var (entry, at) in clashing)
                {
                    try
                    {
                        var opened = await _skipping.OpenAsync(await _skipping.GetEntryAsync(entry.Href, cancellationToken), cancellationToken);
                        existing[at] = entry with { UserName = opened.Content.UserName };
                    }
                    catch (Exception e) when (e is EnvelopeNotOpenedException or PasswordEntryUnreadableException or InvalidOperationException)
                    {
                        // Not one this key opens, or still without content: its user name stays unknown.
                    }
                }
            });
        }

        return new PasswordImportPreview(read, PasswordImport.Conflicts(read.Drafts, existing), existing);
    }

    /// <summary>
    /// Imports what <paramref name="preview"/> read, asking about each conflict in turn (owner decision: per entry); null
    /// from <paramref name="ask"/> cancels the whole import before anything is filed. Answers the names filed.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ImportAsync(PasswordFolderInfo folder, PasswordImportPreview preview,
        Func<PasswordImportConflict, Task<PasswordImportResolution?>> ask, CancellationToken cancellationToken = default)
    {
        var answers = new Dictionary<PasswordImportDraft, PasswordImportResolution>();
        foreach (var conflict in preview.Conflicts)
        {
            if (await ask(conflict) is not { } answer)
            {
                return null;
            }

            answers[conflict.Draft] = answer;
        }

        var steps = PasswordImport.Plan(preview.Read.Drafts, preview.Existing, c => answers[c.Draft]);
        return await Client.ImportAsync(folder, steps, cancellationToken);
    }

    /// <summary>
    /// The sentence to show for a failed password action, in the user's language; null when the user cancelled (the key
    /// dialog's Cancel), which is said by nothing. A 412 is its own sentence (ADR 0794's real UI case).
    /// </summary>
    public static string? Describe(Exception e) => e switch
    {
        // A key not enrolled for the user, the user's keys unknown, or sealed for other keys: the shared words (ADR 0969).
        ISealedRefusal { Outcome: { State: not SealedContentState.Sealed } outcome } => outcome.Text,
        OperationCanceledException => null,
        PasswordEntryChangedException => Strings.Get("PwEntryChanged"),
        PasswordStoreHasNoRecipientsException => Strings.Get("PwNoRecipients"),
        DocumentNameTakenException taken => string.Format(CultureInfo.CurrentCulture, Strings.Get("StNameTaken"), taken.FileName),
        PasswordEntryUnreadableException => Strings.Get("PwEntryUnreadable"),
        HttpRequestException { StatusCode: HttpStatusCode.PreconditionFailed } => Strings.Get("PwEntryChanged"),
        HttpRequestException http => Strings.Get(ServerIdentity.Classify(http).MessageKey),
        _ => e.Message,
    };

    private Task InOneUpdateAsync(Func<Task> work) => oneUpdate is not null ? oneUpdate(work) : work();
}
