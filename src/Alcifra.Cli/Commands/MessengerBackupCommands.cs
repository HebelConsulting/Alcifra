using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Alcifra.Messenger.Client.Api;
using Alcifra.Messenger.Client.Backup;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>What both Messenger commands take: the session, and optionally the user they act as.</summary>
public abstract class MessengerSettings : TenantSessionSettings
{
    [CommandOption("--as <EMAIL>")]
    [Description("Act as this user (impersonation, which you must be allowed): how an administrator moves a user's conversations. The backup stays sealed to THEIR keys.")]
    public string? As { get; init; }
}

public sealed class MessengerBackupSettings : MessengerSettings
{
    [CommandOption("--out <FILE>")]
    [Description("Where to write the backup. An existing file is never overwritten.")]
    public string Out { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(Out) => ValidationResult.Error("Name the backup file with --out <FILE>."),
        _ => ValidationResult.Success(),
    };
}

public sealed class MessengerRestoreSettings : MessengerSettings
{
    [CommandOption("--file <FILE>")]
    [Description("The backup to restore: one written by `alcifra messenger backup` or the phone.")]
    public string File { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(File) => ValidationResult.Error("Name the backup with --file <FILE>."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Writes the signed-in user's Messenger conversations to one sealed file (core #1813, ADR 0945): the ciphertext as
/// delivered and the user's own sealed keys — nothing is decrypted, so the file opens only with their key.
/// </summary>
public sealed class MessengerBackupCommand(ILogger<MessengerBackupCommand> logger) : AsyncCommand<MessengerBackupSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MessengerBackupSettings settings, CancellationToken cancellationToken)
    {
        var (http, storage, api) = await MessengerSession.OpenAsync(settings, logger, cancellationToken);
        using (http)
        using (storage)
        {
            if (System.IO.File.Exists(settings.Out))
            {
                throw new CliException($"{settings.Out} exists already; a backup never overwrites a file.");
            }

            BackupSummary summary;
            await using (var file = new FileStream(settings.Out, FileMode.CreateNew, FileAccess.Write))
            {
                summary = await MessengerSession.RunAsync(() => MessengerBackup.WriteAsync(api, file, settings.ResolvedUrl, cancellationToken));
            }

            logger.LogInformation("Backed up {Conversations} conversations ({Messages} messages, {Files} files) to {File}",
                summary.Conversations, summary.Messages, summary.Files, settings.Out);
            return 0;
        }
    }
}

/// <summary>
/// Restores a Messenger backup for the signed-in user (core #1813, ADR 0945): each conversation as read-only history,
/// joined where another member restored it already; restoring the same file twice changes nothing.
/// </summary>
public sealed class MessengerRestoreCommand(ILogger<MessengerRestoreCommand> logger) : AsyncCommand<MessengerRestoreSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MessengerRestoreSettings settings, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(settings.File))
        {
            throw new CliException($"No such backup: {settings.File}");
        }

        var (http, storage, api) = await MessengerSession.OpenAsync(settings, logger, cancellationToken);
        using (http)
        using (storage)
        {
            RestoreReport report;
            await using (var file = System.IO.File.OpenRead(settings.File))
            {
                report = await MessengerSession.RunAsync(() => MessengerBackup.RestoreAsync(api, file, cancellationToken));
            }

            logger.LogInformation(
                "Restored {Conversations} conversations: {Imported} messages added, {Skipped} already here, {Files} files; {StillHere} still on this installation, left as they are",
                report.Conversations, report.Imported, report.Skipped, report.Files, report.StillHere);
            return 0;
        }
    }
}

// The Messenger's own client, signed in as the session's user. Its address is the archive's root, followed by rel
// (ADR 0543), so nothing here composes a Messenger path; presigned file transfers go through a client with no token.
internal static class MessengerSession
{
    public static async Task<(HttpClient Http, HttpClient Storage, MessengerApi Api)> OpenAsync(
        MessengerSettings settings, ILogger logger, CancellationToken cancellationToken)
    {
        // As another user, by their own token from the exchange: everything below is done on their behalf, and recorded so.
        var token = settings.As is { Length: > 0 } email ? await Impersonation.TokenForAsync(settings, email, cancellationToken) : settings.ResolvedToken;
        if (settings.As is { Length: > 0 } actingAs)
        {
            logger.LogInformation("Acting as {User}", actingAs);
        }

        var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var storage = new HttpClient();
        var api = new MessengerApi(http, storage);
        if (!await RunAsync(() => api.DiscoverAsync(cancellationToken)))
        {
            http.Dispose();
            storage.Dispose();
            throw new CliException("The Messenger is not active on this tenant, or not for you.");
        }

        return (http, storage, api);
    }

    // The Messenger's refusals and a damaged backup, in the tool's own words rather than as a stack trace.
    public static async Task<T> RunAsync<T>(Func<Task<T>> act)
    {
        try
        {
            return await act();
        }
        catch (MessengerApiException refused)
        {
            throw new CliException(refused.Message);
        }
        catch (BackupDamagedException damaged)
        {
            throw new CliException(damaged.Message);
        }
    }
}
