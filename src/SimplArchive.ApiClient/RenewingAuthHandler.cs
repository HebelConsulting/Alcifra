using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimplArchive.ApiClient;

/// <summary>
/// Attaches the bearer token to every request, and renews it before — or, failing that, after — it expires.
/// </summary>
/// <remarks>
/// <para>
/// The client used to capture one access token at login and send it for ever. An hour later every request came
/// back <c>401 (unauthorized)</c> with nothing renewing anything, which reads to the user as the app having
/// gone stale rather than as a session having ended.
/// </para>
/// <para>
/// <b>Both halves are needed.</b> Renewing AHEAD of expiry is what keeps a working session working. Renewing
/// ON a 401 is what recovers the cases the clock cannot predict — a server restarted, a token revoked, a
/// machine that slept through its own expiry. Neither alone is enough: the first assumes the clock is the only
/// reason a token stops working, the second turns every expiry into a user-visible failure first.
/// </para>
/// <para>
/// Renewal is serialised. Twenty parallel requests noticing an expired token at once would otherwise fire
/// twenty refreshes, and with rotation ON (each refresh invalidates the one used) nineteen of them would be
/// presenting a token that the first had already spent — indistinguishable, from the server, from theft.
/// </para>
/// </remarks>
public sealed class RenewingAuthHandler : DelegatingHandler
{
    private readonly string _apiRootUrl;
    private readonly TokenSession.Holder _session;
    private readonly ITokenSessionStore? _store;
    private readonly bool _announce;
    private readonly SemaphoreSlim _ownGate = new(1, 1);

    /// <summary>One gate per installation for every handler over a STORED holder (#1869): the clients of one slot are
    /// separate handlers, so a gate per handler let each of them spend the same refresh token. Keyed by installation
    /// alone, not by slot: two profiles on one server then renew one after the other, which costs a few milliseconds
    /// once a quarter of an hour and needs no slot identity from the host's store.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SharedGates = new(StringComparer.OrdinalIgnoreCase);

    public RenewingAuthHandler(string apiRootUrl, TokenSession.Holder session, HttpMessageHandler inner)
        : this(apiRootUrl, session, inner, store: null, announce: true)
    {
    }

    /// <param name="store">Where a renewed or ended session is recorded; null means the host's
    /// <see cref="ApiClientSettings.Sessions"/>.</param>
    /// <param name="announce">Whether an ended session raises <see cref="SessionEnded"/>. A client working in the
    /// background (a phone's push, for a profile that may not be the one on screen) must not put up the foreground's
    /// sign-in prompt.</param>
    public RenewingAuthHandler(string apiRootUrl, TokenSession.Holder session, HttpMessageHandler inner, ITokenSessionStore? store, bool announce)
        : base(inner)
    {
        _apiRootUrl = apiRootUrl;
        _session = session;
        _store = store;
        _announce = announce;
    }

    private ITokenSessionStore Store => _store ?? ApiClientSettings.Sessions;

    private SemaphoreSlim RenewGate => _session.IsStored
        ? SharedGates.GetOrAdd(_apiRootUrl.TrimEnd('/'), static _ => new SemaphoreSlim(1, 1))
        : _ownGate;

    private void End(string why, HttpRequestMessage? refused = null)
    {
        var ending = _session.Value;
        _session.Value = null;

        // A private holder clears the store's slot only while the slot still holds THIS session (2026-10-23). A client
        // left over from an ended session (the desktop's Messages tab after a sign-in from the "session ended" modal)
        // sent no bearer, was refused, and cleared the slot the new sign-in had just recorded, keychain included.
        if (!_session.IsStored && ending is not null && Store.For(_apiRootUrl) is { } stored && IsSame(stored, ending))
        {
            Store.Clear(_apiRootUrl);
        }

        // Said here because nobody else can (ADR 0626): the modal names the server, not what was refused or why. The
        // path only, never the query, which may carry a credential (the content door's token).
        ApiClientSettings.Logger.LogWarning("The session on {ApiRootUrl} ended: {Why}; refused: {Method} {Path}",
            _apiRootUrl, why, refused?.Method.Method ?? "POST", refused?.RequestUri?.AbsolutePath ?? "/connect/token");

        // Announced once, when it ends: a client whose session had ended already was told then, and putting the modal up
        // again for its next request (while the user may be signed in afresh) is the loop seen on 2026-10-23.
        if (_announce && ending is not null)
        {
            SessionEnded?.Invoke(_apiRootUrl);
        }
    }

    private static bool IsSame(TokenSession stored, TokenSession ending) =>
        stored.RefreshToken is { Length: > 0 } refresh
            ? string.Equals(refresh, ending.RefreshToken, StringComparison.Ordinal)
            : string.Equals(stored.AccessToken, ending.AccessToken, StringComparison.Ordinal);

    /// <summary>
    /// Raised when a session cannot be renewed and the user has to sign in again — with the server named.
    /// </summary>
    /// <remarks>
    /// The profile matters: somebody moving between production, integration and a local stack needs to be told
    /// WHICH one dropped them, and a dialog that only says "your session expired" makes them guess.
    /// </remarks>
    public static event Action<string>? SessionEnded;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var session = _session.Value;

        if (session is { CanRenew: true, NeedsRenewal: true })
        {
            session = await RenewAsync(session, cancellationToken);
        }

        Attach(request, session);
        var sent = session?.AccessToken;

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // A 401 the clock did not predict. Renew ONCE and replay — not in a loop, because a token the server
        // keeps refusing is a session that has ended, and retrying it is just a slower way to say so.
        var current = _session.Value;

        // Another client of the same slot renewed while this request was on the wire (#1869): the session has not
        // ended, only this request's token aged. Replay with theirs rather than ending a session somebody just renewed.
        if (current is { NeedsRenewal: false, AccessToken.Length: > 0 } && !string.Equals(current.AccessToken, sent, StringComparison.Ordinal))
        {
            response.Dispose();
            return await ReplayAsync(request, current, cancellationToken);
        }

        if (current is not { CanRenew: true })
        {
            End(current is null ? "a 401 for a client whose session had already ended (a client left over from it?)" : "a 401 and no refresh token to renew with", request);

            // THROW rather than hand the 401 back (#1251). Returning it made one ordinary expiry produce two
            // racing outcomes: the session-ended modal this event raises, and an HttpRequestException from the
            // caller's EnsureSuccessStatusCode() that nothing caught — which reached the user as a CRASH.
            // AppExceptions.Report recognises this type and stays silent, so the modal above owns the telling.
            response.Dispose();
            throw new SessionEndedException(_apiRootUrl);
        }

        var renewed = await RenewAsync(current, cancellationToken, rejected: sent);
        if (renewed is null || string.IsNullOrEmpty(renewed.AccessToken))
        {
            // Renewal was possible in principle and still failed — the same ended session, reached one step
            // later. It must not return a 401 either, for exactly the reason above.
            if (_session.Value is not null)   // else the refused renewal has just ended it, and said so
            {
                End("a 401 and the renewal did not succeed", request);
            }

            response.Dispose();
            throw new SessionEndedException(_apiRootUrl);
        }

        response.Dispose();
        return await ReplayAsync(request, renewed, cancellationToken);
    }

    private async Task<HttpResponseMessage> ReplayAsync(HttpRequestMessage request, TokenSession session, CancellationToken cancellationToken)
    {
        var replay = await CloneAsync(request, cancellationToken);
        Attach(replay, session);
        return await base.SendAsync(replay, cancellationToken);
    }

    private static void Attach(HttpRequestMessage request, TokenSession? session)
    {
        if (session is { AccessToken.Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }
    }

    /// <summary>Exchanges the refresh token for a new pair, or ends the session when the server refuses.</summary>
    private async Task<TokenSession?> RenewAsync(TokenSession session, CancellationToken cancellationToken, string? rejected = null)
    {
        var gate = RenewGate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have renewed while this one waited at the gate — the common case under load,
            // and the whole point of serialising. Take theirs rather than spending a second refresh token.
            var latest = _session.Value;
            // …but never the token the server has just REFUSED. Without this a 401 before the clock's expiry (a server
            // restarted, a token revoked) found the session "not due", replayed the same token and failed again,
            // with a valid refresh token unused: the very cases renewing on a 401 exists for (found by the shared
            // client's tests, ADR 0917).
            if (latest is { NeedsRenewal: false, AccessToken.Length: > 0 } && !string.Equals(latest.AccessToken, rejected, StringComparison.Ordinal))
            {
                return latest;
            }

            var refreshToken = latest?.RefreshToken ?? session.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                return latest ?? session;
            }

            var next = await RedeemAsync(refreshToken, cancellationToken);

            // Refused — and yet the slot may now hold a DIFFERENT refresh token, written by somebody outside this gate:
            // another process sharing the store, or a fresh sign-in of the same profile meanwhile (#1869). That
            // session has not been refused; end it and the user is signed out of a session that works. Try it once.
            if (next is null && _session.Value is { CanRenew: true } stored && !string.Equals(stored.RefreshToken, refreshToken, StringComparison.Ordinal))
            {
                if (stored is { NeedsRenewal: false, AccessToken.Length: > 0 } && !string.Equals(stored.AccessToken, rejected, StringComparison.Ordinal))
                {
                    return stored;
                }

                next = await RedeemAsync(stored.RefreshToken!, cancellationToken);
            }

            if (next is null)
            {
                // The server has refused this refresh token — expired, revoked, rotated away, or the account
                // deactivated. It will not start working, so the session is over and the stored token goes with
                // it (see TokenSessions.Clear: a token kept here would fail every future launch first).
                End("the server refused the refresh token");
                return null;
            }

            _session.Value = next;

            // The rotated refresh token is also persisted for the SERVER, so the next launch starts signed in. A stored
            // holder has just written it there itself.
            if (!_session.IsStored)
            {
                Store.Set(_apiRootUrl, next);
            }

            return next;
        }
        catch (HttpRequestException)
        {
            // The server is unreachable — a different thing from a refused token, and NOT a reason to throw the
            // stored refresh token away. The connectivity path already owns this case; the session survives so
            // that coming back online resumes rather than re-authenticates.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One refresh-token grant: the new session, or null when the server refused it (or answered no access
    /// token). An unreachable server throws <see cref="HttpRequestException"/>, which is not a refusal.</summary>
    private async Task<TokenSession?> RedeemAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiRootUrl.TrimEnd('/')}/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ApiClientSettings.ClientId,
            }),
        };

        // Sent through the INNER handler: this request carries its own credential and must not be given a
        // bearer, retried on 401, or recursed back into this method.
        using var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var accessToken = tokens.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        // Rotation is on server-side, so a NEW refresh token comes back and the old one is now spent. Keeping
        // the old one would guarantee the next renewal fails.
        var rotated = tokens.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : refreshToken;
        var lifetime = tokens.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

        return new TokenSession(accessToken, rotated, DateTimeOffset.UtcNow + lifetime);
    }

    /// <summary>A replayable copy: a sent request cannot be sent again, and its content stream is consumed.</summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var buffered = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(buffered);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
