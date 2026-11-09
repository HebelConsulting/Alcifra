using System.Text.Json;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

/// <summary>
/// The search-index rebuild as the command reaches it (ADR 1081): every hop a rel, from the root's
/// <c>searchReindex</c> to the run's own <c>self</c>. Apart from the commands so a test drives the same steps.
/// </summary>
public static class SearchReindexSurface
{
    public const string Rel = "searchReindex";

    public static async Task<JsonElement> StatusAsync(AlcifraApi api, CancellationToken cancellationToken) =>
        await api.GetAsync(await new Hypermedia(api).RootHrefAsync(Rel, cancellationToken), cancellationToken);

    public static async Task<JsonElement> RequestAsync(AlcifraApi api, CancellationToken cancellationToken) =>
        await api.PostAsync(await new Hypermedia(api).RootHrefAsync(Rel, cancellationToken), new { }, cancellationToken);

    /// <summary>
    /// Follows a run until it has ended, telling <paramref name="changed"/> each new status. A failed run, and one still
    /// open at <paramref name="deadline"/>, are a <see cref="CliException"/> naming why.
    /// </summary>
    public static async Task<JsonElement> FollowAsync(
        AlcifraApi api, JsonElement run, DateTimeOffset deadline, TimeSpan poll, Action<string> changed, CancellationToken cancellationToken)
    {
        var self = Hypermedia.LinksOf(run).GetValueOrDefault("self")
            ?? throw new CliException("The installation answered the rebuild without an address to follow it by.");
        var seen = Text(run, "status");
        while (true)
        {
            var status = Text(run, "status");
            if (status != seen)
            {
                changed(status);
                seen = status;
            }

            if (status == "succeeded")
            {
                return run;
            }

            if (status == "failed")
            {
                throw new CliException($"The rebuild failed after {run.GetProperty("attempts").GetInt32()} attempt(s): {Text(run, "failure")}");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new CliException(
                    $"The rebuild is still {status}"
                    + (Text(run, "instance") is { Length: > 0 } instance ? $" on instance {instance}" : string.Empty)
                    + ". If that instance has stopped, request a new rebuild.");
            }

            await Task.Delay(poll, cancellationToken);
            run = await api.GetAsync(self, cancellationToken);
        }
    }

    public static string Text(JsonElement resource, string name) =>
        resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
}
