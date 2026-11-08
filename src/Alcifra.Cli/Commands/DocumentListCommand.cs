using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Spectre.Console.Cli;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

public sealed class DocumentListSettings : TenantSessionSettings
{
    [CommandArgument(0, "[ID]")]
    [Description("The folder or repository whose children to list. Without it, the repositories visible to the caller.")]
    public Guid? Parent { get; init; }
}

/// <summary>
/// Lists the repositories, or a document's children, one per line: id, folder or document, its type, its name (ADR 1077).
/// What `document fields`, `folder`, `file` and `reference` take an id from, without a hand-written request (ADR 0871).
/// </summary>
public sealed class DocumentListCommand : AsyncCommand<DocumentListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DocumentListSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var rows = await DocumentFiling.ListAsync(api, new Hypermedia(api), settings.Parent, cancellationToken);
        // Data output (ADR 0906): tab-separated rows a script reads, `cut -f1` for the ids.
        foreach (var row in rows)
        {
            Console.Out.WriteLine($"{row.Id:D}\t{(row.IsFolder ? "folder" : "document")}\t{row.Type}\t{row.Name}");
        }

        return 0;
    }
}
