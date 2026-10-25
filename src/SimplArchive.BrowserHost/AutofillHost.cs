using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimplArchive.BrowserHost;

/// <summary>
/// The native-messaging loop: read a request, answer it, repeat until the browser closes the port.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, two hosts.</b> The desktop and sapass each run this loop; only <see cref="IAutofillStore"/>
/// differs, and the browser cannot tell which one answered beyond the name in <c>ping</c>.
/// </para>
/// <para>
/// <b>A bad request is answered, not fatal.</b> Anything the extension gets wrong comes back as
/// <c>{"ok":false,"error":…}</c> so the page can say something; only a broken FRAME (a length the transport cannot
/// survive) ends the loop. A secret is never logged: commands, ids and reasons are.
/// </para>
/// </remarks>
public sealed class AutofillHost(IAutofillStore store, ILogger logger, string hostName)
{
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonDocument? request;
            try
            {
                request = await NativeMessaging.ReadAsync(input, cancellationToken);
            }
            catch (InvalidDataException e)
            {
                // The frame boundary is lost; nothing after it can be trusted.
                logger.LogWarning("The autofill channel ended on a framing error: {Reason}", e.Message);
                return;
            }

            if (request is null)
            {
                return;
            }

            using (request)
            {
                var answer = await AnswerAsync(request.RootElement, cancellationToken);
                await NativeMessaging.WriteAsync(output, answer, cancellationToken);
            }
        }
    }

    private async Task<object> AnswerAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var id = request.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var value) ? value : 0;
        var command = request.TryGetProperty("command", out var c) ? c.GetString() : null;
        try
        {
            switch (command)
            {
                case "ping":
                    return new { id, ok = true, host = hostName };

                case "match":
                    var matches = await store.MatchAsync(Text(request, "site"), OptionalText(request, "app"), cancellationToken);
                    return new
                    {
                        id,
                        ok = true,
                        entries = matches.Select(m => new { id = m.Id, name = m.Name, path = m.Path, userName = m.UserName }),
                    };

                case "open":
                    if (!Guid.TryParse(OptionalText(request, "entryId"), out var entryId))
                    {
                        return Error(id, "open needs an entryId.");
                    }

                    var credentials = await store.OpenAsync(entryId, cancellationToken)
                        ?? throw new InvalidOperationException("That entry no longer exists.");
                    return new
                    {
                        id,
                        ok = true,
                        password = credentials.Password,
                        userName = credentials.UserName,
                        fields = credentials.Fields.Select(f => new { name = f.Name, value = f.Value }),
                    };

                case "save":
                    var saved = await store.SaveAsync(new AutofillSave(
                        Guid.TryParse(OptionalText(request, "entryId"), out var update) ? update : null,
                        Text(request, "site"),
                        OptionalText(request, "userName") ?? string.Empty,
                        Text(request, "password")), cancellationToken);
                    return new { id, ok = true, entryId = saved.Id, name = saved.Name, path = saved.Path };

                case "generate":
                    return new { id, ok = true, password = store.Generate(Length(request)) };

                default:
                    return Error(id, $"Unknown command '{command}'.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The message goes back to the extension, which shows it; the log keeps the command and the reason, and
            // never a value from the request (a save carries a password).
            logger.LogWarning("Autofill command {Command} failed: {Reason}", command, e.Message);
            return Error(id, e.Message);
        }
    }

    private static object Error(long id, string message) => new { id, ok = false, error = message };

    private static string Text(JsonElement request, string name) =>
        OptionalText(request, name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"'{name}' is required.");

    private static string? OptionalText(JsonElement request, string name) =>
        request.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static int Length(JsonElement request) =>
        request.TryGetProperty("length", out var element) && element.TryGetInt32(out var length) && length is >= 8 and <= 128
            ? length
            : 24;
}
