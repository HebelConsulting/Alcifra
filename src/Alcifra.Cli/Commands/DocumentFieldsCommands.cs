using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

public sealed class DocumentFieldsShowSettings : TenantSessionSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The document whose fields to show.")]
    public required Guid Document { get; init; }
}

/// <summary>Prints a document's mask and its fields by name, one per line (#2258). Data output on stdout.</summary>
public sealed class DocumentFieldsShowCommand : AsyncCommand<DocumentFieldsShowSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DocumentFieldsShowSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var shown = await DocumentFiling.ShowFieldsAsync(api, new Hypermedia(api), settings.Document, cancellationToken);
        // Data output (ADR 0906): the mask, then one Name=Value per value, so the lines read back as --field arguments.
        Console.Out.WriteLine($"mask: {shown.MaskName}");
        // An empty field is printed as Name= so it is seen to exist (setting the easyname Edition's Customer, 2026-11-08).
        foreach (var (name, values) in shown.Fields)
        {
            foreach (var value in values.Count == 0 ? [string.Empty] : values)
            {
                Console.Out.WriteLine($"{name}={value}");
            }
        }

        return 0;
    }
}

public sealed class DocumentFieldsSetSettings : TenantSessionSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The document whose fields to set.")]
    public required Guid Document { get; init; }

    [CommandOption("--field <NAME=VALUE>")]
    [Description("A field of the document's mask and its new value; repeat it for more fields, or for more values of a list field. Fields not named keep their values.")]
    public string[] Fields { get; init; } = [];
}

/// <summary>
/// Sets the named fields of an existing document, a module's document among them (#2258): one PUT of the detail with
/// <c>If-Match</c>, every field not named kept as stored. Nothing goes to stdout.
/// </summary>
public sealed class DocumentFieldsSetCommand(ILogger<DocumentFieldsSetCommand> logger) : AsyncCommand<DocumentFieldsSetSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DocumentFieldsSetSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var fields = settings.Fields.Select(DocumentFiling.ParseField).ToList();
        await DocumentFiling.SetFieldsAsync(api, new Hypermedia(api), settings.Document, fields, cancellationToken);
        logger.LogInformation("{Document}: {Fields} set.", settings.Document, string.Join(", ", fields.Select(f => f.Name).Distinct(StringComparer.Ordinal)));
        return 0;
    }
}
