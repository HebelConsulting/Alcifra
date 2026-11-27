using Alcifra.Application.Identifiers;

namespace Alcifra.Api.Provisioning;

/// <summary>
/// Deterministic ids for seeded demo content (#781): a name-based (RFC 4122 version-5 style) derivation, so a
/// nightly <c>down -v</c> reseeds the SAME archive rather than a new one wearing the same names.
/// </summary>
/// <remarks>
/// <para>
/// The kiosk resets nightly and reseeds from scratch. Every client-visible identity — RFC 8474
/// <c>MAILBOXID</c>/<c>EMAILID</c>, DAV collection ids, <c>UIDVALIDITY</c> — rests on the GUIDs underneath, so
/// fresh GUIDs each morning mean every caching client discards everything it knew. Deriving the ids from
/// stable names makes the reset invisible to a client, which is what a demo of ONE archive requires.
/// </para>
/// <para>
/// Chosen over hard-coded GUID literals (dozens of them, with silent collision as the failure mode) and over
/// hashing display names (renaming a demo document would silently change its identity). The SLUG is the thing
/// that does not change when the name does, and the tenant id is inside the hash so a second demo tenant stays
/// distinct.
/// </para>
/// <para>
/// <b>The namespace and the algorithm are frozen.</b> The whole point is that the same inputs produce the same
/// GUID across releases — <c>DemoIdTests</c> pins golden outputs as literals, and a change here fails them.
/// Failing that test does not mean the test is stale; it means the change breaks every client cache and every
/// bookmarked id on the next kiosk reset, and needs to be that deliberate.
/// </para>
/// </remarks>
public static class DemoId
{
    /// <summary>The fixed Alcifra demo-seed namespace. Never change it (see remarks).</summary>
    private static readonly Guid Namespace = Guid.Parse("5A21B5E0-DE20-4E11-9D5B-8F1C2E40D0D5");

    /// <summary>The demo tenant's own id, derived from its configured name.</summary>
    public static Guid Root(string tenantName) => Derive(Namespace, $"tenant/{tenantName}");

    /// <summary>An id owned by the demo tenant, derived from a stable slug (never from a display name).</summary>
    public static Guid For(Guid tenantId, string slug) => Derive(tenantId, slug);

    private static Guid Derive(Guid namespaceId, string name) => DeterministicIds.V5(namespaceId, name);
}
