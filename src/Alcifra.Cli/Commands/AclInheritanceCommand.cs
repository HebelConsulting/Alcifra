using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Makes a document's OWN grants the ones that apply to it, instead of its ancestors' (ADR 0966).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a grant needs this first.</b> Rights are resolved at the governing scope (ADR 0183): the nearest document,
/// walking up, that breaks inheritance, else the repository. A grant written on a document that inherits is stored and
/// then ignored, so <c>acl set</c> on a fresh folder appears to work and changes nothing. Breaking copies the grants in
/// force down onto the document (nobody loses or gains anything), and from then on its own grants are the ones that count.
/// </para>
/// <para>
/// Through the document's advertised <c>acl-inheritance</c> rel under the document's ETag (ADR 0794); idempotent, so a
/// document already breaking is reported unchanged. Restoring inheritance discards the document's own grants, which is
/// a different act and deliberately not offered here.
/// </para>
/// </remarks>
public sealed class AclBreakInheritanceCommand(ILogger<AclBreakInheritanceCommand> logger) : AsyncCommand<AclDocumentSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, AclDocumentSettings settings, CancellationToken cancellationToken)
    {
        using var http = TenantHttp.Client(settings);
        var broke = await BreakAsync(new AlcifraApi(http), Guid.Parse(settings.Document), cancellationToken);
        if (broke)
        {
            logger.LogInformation("{Document} now uses its own grants (the inherited ones were copied onto it).", settings.Document);
        }
        else
        {
            logger.LogInformation("{Document} already uses its own grants; unchanged.", settings.Document);
        }

        return 0;
    }

    /// <summary>Breaks inheritance on <paramref name="documentId"/>; false when it already did.</summary>
    public static async Task<bool> BreakAsync(AlcifraApi api, Guid documentId, CancellationToken cancellationToken)
    {
        var template = await new Hypermedia(api).RootHrefAsync("document", cancellationToken);
        var (document, etag) = await api.GetWithETagAsync(
            template.Replace("{id}", documentId.ToString("D"), StringComparison.Ordinal), cancellationToken);

        if (document.TryGetProperty("breaksInheritance", out var breaks) && breaks.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        // A missing rel is the answer (ADR 0543): a repository root has nothing to inherit from, and only a caller who
        // may manage permissions is offered it.
        var href = Hypermedia.LinksOf(document).TryGetValue("acl-inheritance", out var advertised)
            ? advertised
            : throw new CliException(
                $"{documentId:D} offers no way to change its inheritance: either it is a repository (which has nothing to "
                + "inherit from), or the signed-in principal may not manage permissions on it.");

        await api.PutWithETagAsync(href, new { breaksInheritance = true }, etag, cancellationToken);
        return true;
    }
}
