using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

public sealed class DocumentDeleteSettings : TenantSessionSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The document to move to the recycle bin, with everything under it.")]
    public required Guid Document { get; init; }

    [CommandOption("--yes")]
    [Description("Confirms the deletion. Without it nothing is deleted.")]
    public bool Yes { get; init; }
}

/// <summary>Moves a document and its subtree to the recycle bin (ADR 1078). Restorable from the bin; nothing is purged.</summary>
public sealed class DocumentDeleteCommand(ILogger<DocumentDeleteCommand> logger) : AsyncCommand<DocumentDeleteSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DocumentDeleteSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Yes)
        {
            throw new CliException($"Nothing deleted: add --yes to move {settings.Document:D} and everything under it to the recycle bin.");
        }

        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var name = await DocumentFiling.DeleteAsync(api, new Hypermedia(api), settings.Document, cancellationToken);
        logger.LogInformation("{Name} ({Document}) moved to the recycle bin.", name, settings.Document);
        return 0;
    }
}
