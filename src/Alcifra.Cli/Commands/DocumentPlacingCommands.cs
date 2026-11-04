using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class DocumentFolderSettings : TenantSessionSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("The folder's name. A folder of exactly this name under the parent is reused.")]
    public required string Name { get; init; }

    [CommandOption("--parent <ID>")]
    [Description("The folder or repository to create it under.")]
    public required Guid Parent { get; init; }

    [CommandOption("--mask <MASK>")]
    [Description("The folder's mask, by name or id.")]
    public required string Mask { get; init; }

    [CommandOption("--field <NAME=VALUE>")]
    [Description("A field of the mask and its value; repeat it for more fields, or for more values of a list field.")]
    public string[] Fields { get; init; } = [];
}

/// <summary>Creates a folder wearing a mask and prints its id on stdout (ADR 1046). Re-runnable.</summary>
public sealed class DocumentFolderCommand(ILogger<DocumentFolderCommand> logger) : AsyncCommand<DocumentFolderSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DocumentFolderSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var id = await DocumentFiling.FolderAsync(
            api, new Hypermedia(api), settings.Parent, settings.Name.Trim(), settings.Mask.Trim(),
            [.. settings.Fields.Select(DocumentFiling.ParseField)], cancellationToken);
        logger.LogInformation("{Name}: folder ready.", settings.Name);

        // Data output (ADR 0906): the id alone on stdout, for `id=$(alcifra document folder …)`.
        Console.Out.WriteLine(id.ToString("D"));
        return 0;
    }
}

public sealed class DocumentReferenceSettings : TenantSessionSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The document to reference.")]
    public required Guid Target { get; init; }

    [CommandOption("--into <ID>")]
    [Description("The folder to place the reference in. A reference already there is left alone.")]
    public required Guid Into { get; init; }
}

/// <summary>Places a reference to a document in a folder (ADR 1046). Re-runnable; nothing goes to stdout.</summary>
public sealed class DocumentReferenceCommand(ILogger<DocumentReferenceCommand> logger) : AsyncCommand<DocumentReferenceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DocumentReferenceSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var placed = await DocumentFiling.PlaceReferenceAsync(api, new Hypermedia(api), settings.Target, settings.Into, cancellationToken);
        logger.LogInformation("Reference to {Target} in {Into}: {Outcome}.", settings.Target, settings.Into, placed ? "placed" : "already there");
        return 0;
    }
}
