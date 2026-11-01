using System.ComponentModel;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Presentation;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

public sealed class ShowSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking.")]
    public string Entry { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: apass show <entry>.")
        : ValidationResult.Success();
}

/// <summary>
/// Prints an entry's fields IN THE FORM'S ORDER, each labelled: URL, user name, password, PIN, verification code, then the
/// rest (<see cref="PasswordEntryFieldOrder"/>, ADR 0964; owner, 2026-11-01, replacing the password-first line of
/// 2026-10-31). One secret alone, for a script, is <c>apass password</c> or <c>apass otp</c>.
/// </summary>
/// <remarks>
/// <b>This is the one command that touches the key.</b> The envelope is opened on this machine by the card in the
/// reader, through the same opener the desktop uses; the PIN is typed at the terminal and is not kept — the card
/// session holds the LOGIN, not the secret (ADR 0832).
/// </remarks>
public sealed class ShowCommand : AsyncCommand<ShowSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ShowSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(new CardOpener());
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);
        var rows = await catalog.RowsAsync(store, settings.Refresh, cancellationToken);

        var row = StoreCatalog.Find(rows, settings.Entry);
        if (row.IsFolder)
        {
            throw new SapassException($"'{row.Path}' is a folder; name an entry.");
        }

        var entry = await session.Passwords.GetEntryAsync(row.Href, cancellationToken);
        var opened = await session.Passwords.OpenAsync(entry, cancellationToken);

        // Data output (ADR 0906): the whole entry, the password and the PIN among its lines, as the command exists to show.
        foreach (var line in FieldLines(opened.Content, TimeProvider.System))
        {
            Console.Out.WriteLine(line);
        }

        return 0;
    }

    /// <summary>Every field in the form's order, labelled by its stored name, the password among them.</summary>
    /// <remarks>
    /// The same rule every client shows an entry by (<see cref="PasswordEntryFieldOrder.Arrange{T}"/>), so the terminal
    /// lists the fields as the form does. A verification code's setup is a SECRET (an <c>otpauth://</c> link holding
    /// the key), so it is shown as the code valid now with its seconds left, as every client shows it, never as itself.
    /// </remarks>
    internal static IEnumerable<string> FieldLines(PasswordEntryContent content, TimeProvider clock)
    {
        var fields = content.Fields.Select(f => (Name: f.Name, f.Value));
        if (content.Password.Length > 0)
        {
            fields = fields.Prepend(("Password", content.Password));   // the order places it third, as the form does
        }

        foreach (var (name, value) in PasswordEntryFieldOrder.Arrange(fields, f => f.Name))
        {
            yield return PasswordEntryFieldOrder.SlotOf(name) == PasswordFieldSlot.VerificationCode
                ? $"{name}: {(VerificationCodeSetup.TryParse(value, out var setup) ? CodeText(setup!.Current(clock)) : "(cannot be read)")}"
                : $"{name}: {value}";
        }
    }

    private static string CodeText(VerificationCode code) => $"{code.Value} (valid {code.SecondsLeft} s)";
}
