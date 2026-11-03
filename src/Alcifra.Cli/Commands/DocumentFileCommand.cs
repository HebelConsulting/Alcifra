using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class DocumentFileSettings : TenantSessionSettings
{
    [CommandArgument(0, "<PATH>")]
    [Description("The local file to file.")]
    public required string Path { get; init; }

    [CommandOption("--parent <ID>")]
    [Description("The folder or repository to file it under.")]
    public required Guid Parent { get; init; }

    [CommandOption("--name <NAME>")]
    [Description("The document's name; the file's name without its extension when omitted. A document of exactly this name under the parent is reused.")]
    public string? Name { get; init; }

    [CommandOption("--mask <MASK>")]
    [Description("The mask to assign, by name or id. Without it the server classifies the document as for any upload.")]
    public string? Mask { get; init; }

    [CommandOption("--field <NAME=VALUE>")]
    [Description("A field of the mask and its value; repeat it for more fields, or for more values of a list field. The fields given are the document's index data.")]
    public string[] Fields { get; init; } = [];

    public override ValidationResult Validate()
    {
        var baseResult = base.Validate();
        if (!baseResult.Successful)
        {
            return baseResult;
        }

        if (!File.Exists(Path))
        {
            return ValidationResult.Error($"No file at '{Path}'.");
        }

        return Fields.Length > 0 && string.IsNullOrWhiteSpace(Mask)
            ? ValidationResult.Error("--field needs the --mask that declares it.")
            : ValidationResult.Success();
    }
}

/// <summary>
/// Files a local file as a document, with a mask and its index data, and prints the document's id on stdout
/// (ADR 1040). Re-runnable: a document of that name under the parent is reused, its mask and fields stated again,
/// and new content filed only when the bytes differ.
/// </summary>
public sealed class DocumentFileCommand(ILogger<DocumentFileCommand> logger) : AsyncCommand<DocumentFileSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, DocumentFileSettings settings, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        var api = new AlcifraApi(http);
        var name = string.IsNullOrWhiteSpace(settings.Name)
            ? System.IO.Path.GetFileNameWithoutExtension(settings.Path)
            : settings.Name.Trim();
        var (id, outcome) = await DocumentFiling.FileAsync(
            api,
            new Hypermedia(api),
            settings.Parent,
            name,
            System.IO.Path.GetExtension(settings.Path),
            await File.ReadAllBytesAsync(settings.Path, cancellationToken),
            string.IsNullOrWhiteSpace(settings.Mask) ? null : settings.Mask.Trim(),
            [.. settings.Fields.Select(DocumentFiling.ParseField)],
            cancellationToken);
        logger.LogInformation("{Name}: {Outcome}.", name, outcome switch
        {
            LicenceFiling.Outcome.Filed => "filed",
            LicenceFiling.Outcome.NewVersion => "filed as a new version",
            _ => "content unchanged",
        });

        // Data output (ADR 0906): the id alone on stdout, for `id=$(alcifra document file …)`.
        Console.Out.WriteLine(id.ToString("D"));
        return 0;
    }
}
