using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;
using Alcifra.Pass.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

/// <summary>
/// Which installation the shared session (ADR 1071) points at, as whom, and which of the person's hardware keys the
/// installation knows, and which stores it may read: the questions behind most "could not be read" and empty answers.
/// </summary>
public sealed class WhoAmICommand(ILogger<WhoAmICommand> logger) : AsyncCommand<CommandSettings>
{
    /// <summary>What the session is, read once: who, and the person's enrolled keys.</summary>
    /// <param name="Stores">The stores the person may read, each as <c>--store</c> accepts it.</param>
    internal sealed record Identity(string Installation, WhoAmIInfo Me, IReadOnlyList<EnrolledCertificatesClient.Enrolled> Keys, IReadOnlyList<string> Stores);

    protected override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings, CancellationToken cancellationToken)
    {
        using var session = Session.FromEnvironment();
        logger.LogInformation("{Identity}", Describe(await ReadAsync(session, cancellationToken)));
        return 0;
    }

    internal static async Task<Identity> ReadAsync(Session session, CancellationToken cancellationToken)
    {
        var (stores, offersMyPasswords) = await session.Passwords.ListStoresAsync(cancellationToken);
        return new(session.Url,
            await new WhoAmIClient(session.Core).GetAsync(cancellationToken),
            await new EnrolledCertificatesClient(session.Core).GetMineAsync(cancellationToken),
            StoreCatalog.CompletionNames(stores, offersMyPasswords));
    }

    /// <summary>The lines a person reads, aligned as the login prints its own.</summary>
    internal static string Describe(Identity identity)
    {
        var usable = identity.Keys.Count(k => k.Usable && k.RevokedAt is null);
        var keys = identity.Keys.Count == 0
            ? "none enrolled: a store's entries cannot be opened"
            : $"{identity.Keys.Count} enrolled, {usable} usable ({string.Join(", ", identity.Keys.Select(k => k.Label))})";
        return string.Join('\n',
            $"Installation    {identity.Installation}",
            $"Signed in as    {identity.Me.UserName ?? "(unknown)"}",
            $"Tenant          {identity.Me.TenantName ?? "(none)"}",
            $"Hardware keys   {keys}",
            $"Stores          {string.Join(", ", identity.Stores)}");
    }
}
