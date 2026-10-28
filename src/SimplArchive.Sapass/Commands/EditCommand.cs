using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Sapass.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

public sealed class EditSettings : StoreSettings
{
    [CommandArgument(0, "<entry>")]
    [Description("The entry's path, e.g. Mail/Banking.")]
    public string Entry { get; init; } = string.Empty;

    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(Entry)
        ? ValidationResult.Error("Name the entry: apass edit Mail/Banking.")
        : ValidationResult.Success();
}

/// <summary>
/// Opens an entry in <c>$EDITOR</c> (else <c>$VISUAL</c>, else <c>vi</c>/<c>notepad</c>) and files what is saved as a
/// new version, under the precondition of the read that filled the file (ADR 0794).
/// </summary>
/// <remarks>
/// <para>
/// The file is the same text <c>show</c> prints: the password, then one <c>Name: value</c> per field. Opening it
/// needs the card and its PIN — the content is sealed and only the key reads it.
/// </para>
/// <para>
/// <b>The temporary file is a secret on disk.</b> It is created owner-only where the platform supports it, read
/// back immediately, and deleted in a <c>finally</c> — including when the editor fails. A changed entry writes a new
/// version under the ETag from the read, so somebody else's save is a 412 rather than an overwrite.
/// </para>
/// </remarks>
public sealed class EditCommand(ILogger<EditCommand> logger) : AsyncCommand<EditSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EditSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment(new CardOpener());
        var catalog = new StoreCatalog(session);
        var store = await catalog.StoreAsync(settings.Store, cancellationToken);
        var rows = await catalog.RowsAsync(store, settings.Refresh, cancellationToken);

        var row = StoreCatalog.Find(rows, settings.Entry);
        if (row.IsFolder)
        {
            throw new SapassException($"'{row.Path}' is a folder; edit works on entries.");
        }

        var entry = await session.Passwords.GetEntryAsync(row.Href, cancellationToken);
        var opened = await session.Passwords.OpenAsync(entry, cancellationToken);
        var before = EntryText.Format(opened.Content);

        var edited = Edit(before);
        if (string.Equals(edited, before, StringComparison.Ordinal))
        {
            logger.LogInformation("{Entry} was left unchanged.", row.Path);
            return 0;
        }

        var saved = await session.Passwords.SaveEntryAsync(opened, EntryText.Parse(edited, opened.Content.Fields), cancellationToken);
        logger.LogInformation("Saved {Entry} as version {Version}.", row.Path, saved.VersionNumber);
        return 0;
    }

    /// <summary>Opens the text in the user's editor and returns what was left; the temp file is always removed.</summary>
    private static string Edit(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sapass-{Guid.NewGuid():N}.txt");
        try
        {
            // Owner-only FROM THE FIRST BYTE where the platform can say so: the file holds a secret until it is
            // removed a moment later, and a Create-then-chmod leaves a window where it does not.
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(path, text);
            }
            else
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };
                using var stream = new FileStream(path, options);
                using var writer = new StreamWriter(stream);
                writer.Write(text);
            }

            var editor = Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual ? visual
                : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } editorValue ? editorValue
                : OperatingSystem.IsWindows() ? "notepad" : "vi";

            var parts = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var start = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
            foreach (var argument in parts.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(path);

            using var process = Process.Start(start)
                ?? throw new SapassException($"Could not start the editor '{editor}'. Set $EDITOR, or edit the entry by another means.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new SapassException($"The editor '{editor}' exited with {process.ExitCode}; nothing was saved.");
            }

            return File.ReadAllText(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException e)
            {
                // Never the reason a save fails: the file is owner-only and in the temp directory, but say so.
                SapassLog.For(nameof(EditCommand)).LogWarning("Could not remove the temporary edit file {Path}: {Reason}", path, e.Message);
            }
        }
    }
}
