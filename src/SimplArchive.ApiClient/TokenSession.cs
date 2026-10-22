namespace SimplArchive.ApiClient;

/// <summary>One server profile's tokens: what to send, what to renew with, and when to renew.</summary>
/// <param name="AccessToken">The bearer sent on every request.</param>
/// <param name="RefreshToken">
/// What renews it without the user present, or null when the server issued none — an older deployment whose
/// client registration predates the refresh grant, for instance.
/// </param>
/// <param name="ExpiresAt">
/// When the access token stops working, as an instant rather than a duration: a duration read at login is
/// already wrong by the time anyone consults it, and a laptop that slept for an hour makes it wrong by an hour.
/// </param>
public sealed record TokenSession(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// One client's live session — mutable, because renewal replaces it while the client keeps working.
    /// </summary>
    /// <remarks>
    /// Owned by the api client rather than looked up from the shared store on every request. The store is keyed
    /// by SERVER, which is right for persistence and wrong for identity: two clients for different users
    /// against the same server would share one slot, and the second to be built would silently become the
    /// first. That is not a test-only concern — impersonation (ADR 0354) is exactly two identities against one
    /// server — but the tests are what surfaced it, as 115 failures and then two subtler ones where a user
    /// read another user's personal repository.
    /// <para>
    /// <b>A holder over a store is the exception, and it is the phone's every client (#1869).</b> A client bound to ONE
    /// slot (<see cref="ApiCore.Bound"/>: one profile, one installation) has no identity question to answer — the slot
    /// IS the identity — and there a private copy is the defect: the phone holds several clients of one profile at once
    /// (the page on screen, a push's fetch, the scheduled backup, a revoke), and each copy went stale the moment another
    /// renewed or ended. One spent a refresh token the other then presented again; one ended and cleared the slot under
    /// the other. So <see cref="Stored"/> keeps no copy at all: every read is the slot's current session, and every
    /// renewal is seen by every client of it.
    /// </para>
    /// </remarks>
    public sealed class Holder
    {
        private readonly ITokenSessionStore? _store;
        private readonly string _apiRootUrl = string.Empty;
        private TokenSession? _value;

        public Holder(TokenSession? initial) => _value = initial;

        private Holder(ITokenSessionStore store, string apiRootUrl)
        {
            _store = store;
            _apiRootUrl = apiRootUrl;
        }

        /// <summary>A holder that IS the store's slot for <paramref name="apiRootUrl"/>: no copy, so every client over
        /// that slot shares one session (#1869).</summary>
        public static Holder Stored(ITokenSessionStore store, string apiRootUrl)
        {
            ArgumentNullException.ThrowIfNull(store);
            return new Holder(store, apiRootUrl);
        }

        /// <summary>Whether this holder reads and writes the store's slot rather than a private copy.</summary>
        public bool IsStored => _store is not null;

        public TokenSession? Value
        {
            get => _store is null ? _value : _store.For(_apiRootUrl);
            set
            {
                if (_store is null)
                {
                    _value = value;
                }
                else if (value is null)
                {
                    _store.Clear(_apiRootUrl);
                }
                else
                {
                    _store.Set(_apiRootUrl, value);
                }
            }
        }
    }

    /// <summary>How far ahead of expiry a renewal is due.</summary>
    /// <remarks>
    /// A minute, so a request that is about to be sent does not race its own token across the wire. Renewing
    /// exactly at expiry guarantees a population of requests that leave valid and arrive expired.
    /// </remarks>
    public static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(1);

    /// <summary>Whether the token is close enough to expiry to be replaced before the next request.</summary>
    /// <remarks>
    /// Written as "does the expiry fall inside the window ahead of us" rather than "is now past expiry minus
    /// the window", because the second UNDERFLOWS: a restored session carries DateTimeOffset.MinValue, and
    /// subtracting a minute from it throws. That is the "still signed in from last launch" path, so the
    /// arithmetic would have thrown on the first request of every restored session.
    /// </remarks>
    public bool NeedsRenewal => ExpiresAt <= DateTimeOffset.UtcNow + RenewAhead;

    /// <summary>A session with no usable renewal path — the user has to sign in again.</summary>
    public bool CanRenew => !string.IsNullOrEmpty(RefreshToken);
}
