using System.Text.Json;
using Alcifra.Presentation;
using Microsoft.Extensions.Logging;

namespace Alcifra.ApiClient.Login;

/// <summary>What the session file holds. The client id is the tool that signed in: the other tool renews with it.</summary>
public sealed record CliSessionState(string Url, string ClientId, string? AccessToken, string? RefreshToken, DateTimeOffset RenewedAt);

/// <summary>
/// The person's session both command-line tools share, in one file only they can read (ADR 1071): <c>alcifra login</c> or
/// <c>apass login</c> writes it, every command of either tool reads it and renews it. A session ends after 15 idle minutes
/// and at the latest 12 hours after its login; the server holds both limits.
/// </summary>
/// <remarks>
/// <para>
/// <b>The environment wins.</b> A token in <c>ALCIFRA_TOKEN</c> is used as it is and never renewed: a script's or a service
/// account's session. A URL in <c>ALCIFRA_URL</c> naming another installation keeps the file out of it, so the file's token
/// is never sent to a host it was not issued by.
/// </para>
/// <para>
/// <b>Renewal rotates the refresh token, so parallel commands take turns</b> under a lock, and a command finding a session
/// renewed moments ago uses it as it is: <c>apass ls | xargs -n1 apass show</c> is not one renewal per process.
/// </para>
/// </remarks>
public sealed class CliSession(string path, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
{
    /// <summary>A session renewed this recently is used as it is.</summary>
    private static readonly TimeSpan RenewedRecently = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LockPatience = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Transport = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    // What this process itself put into the environment, which is therefore not the shell's word (a long-lived host loads
    // the session again before each request).
    private static string? s_loadedToken;

    /// <summary>The file: <c>ALCIFRA_SESSION_FILE</c>, else <c>alcifra/session.json</c> in the platform's per-user data folder.</summary>
    public static CliSession Default => new(DefaultPath);

    private static string DefaultPath =>
        Environment.GetEnvironmentVariable(WireNames.SessionFileVariable) is { Length: > 0 } given
            ? given
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "alcifra", "session.json");

    /// <summary>
    /// Puts the file's session into this process's environment, where every command already looks.
    /// </summary>
    /// <param name="renew">False for what must cost no request and extend nothing: tab completion.</param>
    public static async Task LoadIntoEnvironmentAsync(bool renew, CancellationToken cancellationToken = default)
    {
        var token = Environment.GetEnvironmentVariable(WireNames.TokenVariable);
        var shellToken = token == s_loadedToken ? null : token;
        var state = await Default.ResolveAsync(Environment.GetEnvironmentVariable(WireNames.UrlVariable), shellToken, renew, cancellationToken);
        if (state is null)
        {
            return;
        }

        s_loadedToken = state.AccessToken;
        Environment.SetEnvironmentVariable(WireNames.UrlVariable, state.Url);
        Environment.SetEnvironmentVariable(WireNames.TokenVariable, state.AccessToken);
    }

    /// <summary>
    /// The session to use, renewed if asked; null where the environment's session applies or there is no file. A session
    /// the server refused to renew comes back without tokens, its URL kept, so the tool can name where to sign in again.
    /// An unreachable server changes nothing: the current token is tried, and answers for itself.
    /// </summary>
    public async Task<CliSessionState?> ResolveAsync(string? shellUrl, string? shellToken, bool renew, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(shellToken) || Read() is not { } first || NamesAnother(shellUrl, first.Url))
        {
            return null;
        }

        if (!renew || first.RefreshToken is null)
        {
            return first;
        }

        using var held = await TryLockAsync(cancellationToken);
        if (held is null)
        {
            return first;
        }

        // Read again under the lock: whoever held it has spent the refresh token this process read.
        var state = Read() ?? first;
        if (state.RefreshToken is null || DateTimeOffset.UtcNow - state.RenewedAt < RenewedRecently)
        {
            return state;
        }

        try
        {
            var renewed = await RefreshGrant.RedeemAsync(send ?? Transport.SendAsync, state.Url, state.RefreshToken, state.ClientId, cancellationToken);
            var next = renewed is null
                ? state with { AccessToken = null, RefreshToken = null }
                : state with { AccessToken = renewed.AccessToken, RefreshToken = renewed.RefreshToken, RenewedAt = DateTimeOffset.UtcNow };
            Write(next);
            return next;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            ApiClientSettings.Logger.LogDebug("The session on {Url} was not renewed: {Reason}", state.Url, e.Message);
            return state;
        }
    }

    /// <summary>The sentence a login owes where the shell's variables would override what it just saved, or null.</summary>
    public static string? ShellOverrideWarning(string? shellUrl, string? shellToken, string savedUrl) =>
        !string.IsNullOrWhiteSpace(shellToken) || NamesAnother(shellUrl, savedUrl)
            ? $"This shell still sets {WireNames.UrlVariable} or {WireNames.TokenVariable}, which override this login. Run: unset {WireNames.UrlVariable} {WireNames.TokenVariable}"
            : null;

    /// <summary>Keeps a new login, replacing the one before it.</summary>
    public void Save(string url, string clientId, TokenSession session) =>
        Write(new CliSessionState(url.TrimEnd('/'), clientId, session.AccessToken, session.RefreshToken, DateTimeOffset.UtcNow));

    /// <summary>The session as stored, or null when there is none or it cannot be read.</summary>
    public CliSessionState? Read()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CliSessionState>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            ApiClientSettings.Logger.LogDebug("Ignoring an unreadable session file at {Path}: {Reason}", path, e.Message);
            return null;
        }
    }

    /// <summary>Ends the session: revoked on the server where it can be reached, and gone from this machine either way.</summary>
    /// <returns>The installation it was on, or null when there was no session.</returns>
    public async Task<string?> EndAsync()
    {
        if (Read() is not { } state)
        {
            return null;
        }

        File.Delete(path);
        await TokenRevocation.RevokeAsync(state.Url, state.RefreshToken, state.AccessToken, state.ClientId);
        return state.Url;
    }

    private static bool NamesAnother(string? shellUrl, string url) =>
        !string.IsNullOrWhiteSpace(shellUrl) && !string.Equals(shellUrl.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase);

    // Written beside the target and moved over it, so a reader never sees half a file, and created readable by nobody else.
    private void Write(CliSessionState state)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var file = new StreamWriter(temporary, options))
        {
            file.Write(JsonSerializer.Serialize(state));
        }

        File.Move(temporary, path, overwrite: true);
    }

    private async Task<FileStream?> TryLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + LockPatience;
        while (true)
        {
            try
            {
                return new FileStream($"{path}.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ApiClientSettings.Logger.LogDebug("The session file's lock was not available: {Reason}", e.Message);
                return null;
            }
        }
    }
}
