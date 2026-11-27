using Alcifra.Application.Identifiers;

namespace Alcifra.Api.Documents;

/// <summary>
/// The identity a caller chooses by slug (#2501): a create that carries an <c>idSlug</c> gets a GUID derived from the
/// tenant and the slug, so a re-runnable seed keeps its ids across a reset — the same property the demo seeder has
/// had since #781, for content no seeder owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>One algorithm, two namespaces.</b> The derivation is the demo seeder's (<see cref="DeterministicIds.V5"/>), but
/// under a namespace of its own, frozen here. Sharing the demo seeder's namespace would let a caller's slug resolve to
/// a demo-seeded id in the same tenant — a collision the seeder cannot avoid — so the two never meet.
/// </para>
/// <para>
/// The tenant id is part of the hashed name: document ids are unique across the whole installation, and a slug is the
/// caller's own vocabulary, so two tenants saying <c>folder/contracts</c> must not become one id.
/// </para>
/// </remarks>
public static class CallerDocumentIds
{
    /// <summary>The fixed caller-slug namespace. Never change it (see the remarks on <see cref="DeterministicIds"/>).</summary>
    private static readonly Guid Namespace = Guid.Parse("9C41D3A0-7E62-4F1A-8D0B-3B5C9E607214");

    /// <summary>The id a slug names in <paramref name="tenantId"/>; the caller's slug is trimmed by the caller's door.</summary>
    public static Guid For(Guid tenantId, string slug) => DeterministicIds.V5(Namespace, $"{tenantId:N}:{slug}");
}
