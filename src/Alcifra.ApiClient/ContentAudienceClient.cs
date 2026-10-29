using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Alcifra.ApiClient;

/// <summary>One declared row of an audience: a user or a group, by name (ADR 0983).</summary>
/// <param name="InEffect">For a user, whether they are in the EFFECTIVE audience (active and a reader); false shows a
/// member who lost read access. For a group, whether any of its members is.</param>
public sealed record AudienceMemberInfo(Guid? UserId, Guid? GroupId, string DisplayName, bool IsManager, bool InEffect)
{
    public bool IsGroup => GroupId is not null;
}

/// <summary>One person in the effective audience, with the certificates content is sealed to for them (possibly none).</summary>
public sealed record AudienceRecipientInfo(Guid UserId, string DisplayName, bool IsManager, IReadOnlyList<string> CertificatePems);

/// <summary>A reader who could be added: a user, or a group with at least one reader.</summary>
public sealed record AudienceCandidateInfo(Guid? UserId, Guid? GroupId, string DisplayName)
{
    public bool IsGroup => GroupId is not null;
}

/// <summary>One row of an audience to save: exactly one of user and group, a manager only ever a user.</summary>
public sealed record AudienceChange(Guid? UserId, Guid? GroupId, bool IsManager)
{
    public static AudienceChange Of(AudienceMemberInfo member) => new(member.UserId, member.GroupId, member.IsManager);

    public static AudienceChange Of(AudienceCandidateInfo candidate) => new(candidate.UserId, candidate.GroupId, false);
}

/// <summary>
/// The audience of encrypted content (ADR 0983): who a Password Store or a Sealed document is meant to be decrypted by,
/// who may change that, and who it is sealed to now.
/// </summary>
/// <param name="ETag">The DECLARING DOCUMENT's tag: the precondition a save carries.</param>
/// <param name="CanManage">The caller is an effective manager and may change members and managers.</param>
/// <param name="CanAppointManager">No manager is left and the caller is a tenant administrator: they may appoint one.</param>
public sealed record ContentAudienceInfo(
    Guid DocumentId, bool IsStore, bool IsPersonal, string ETag, bool CanManage, bool CanAppointManager, bool IsMember,
    IReadOnlyList<AudienceMemberInfo> Members, IReadOnlyList<AudienceRecipientInfo> Recipients,
    IReadOnlyList<AudienceCandidateInfo> Candidates, LinkMap Links)
{
    /// <summary>Whether the caller may save a change at all.</summary>
    public bool CanChange => CanManage || CanAppointManager;

    /// <summary>Every certificate content must be sealed to, each once: what a client seals with after a change.</summary>
    public IReadOnlyList<X509Certificate2> Certificates() =>
        [.. Recipients.SelectMany(r => r.CertificatePems).Distinct(StringComparer.Ordinal).Select(pem => X509Certificate2.CreateFromPem(pem))];

    /// <summary>Members in effect who hold no certificate yet: nothing can be sealed to them until they enrol one.</summary>
    public IReadOnlyList<AudienceRecipientInfo> WithoutCertificate => [.. Recipients.Where(r => r.CertificatePems.Count == 0)];

    internal static ContentAudienceInfo From(JsonElement json, string? eTag = null) => new(
        json.GetProperty("documentId").GetGuid(),
        json.GetProperty("kind").GetString() == "password-store",
        json.TryGetProperty("isPersonal", out var p) && p.ValueKind == JsonValueKind.True,
        eTag ?? json.GetProperty("eTag").GetString() ?? string.Empty,
        json.TryGetProperty("canManage", out var m) && m.ValueKind == JsonValueKind.True,
        json.TryGetProperty("canAppointManager", out var a) && a.ValueKind == JsonValueKind.True,
        json.TryGetProperty("isMember", out var i) && i.ValueKind == JsonValueKind.True,
        [.. json.GetProperty("members").EnumerateArray().Select(e => new AudienceMemberInfo(
            OptionalGuid(e, "userId"), OptionalGuid(e, "groupId"), e.GetProperty("displayName").GetString() ?? string.Empty,
            e.GetProperty("isManager").GetBoolean(), e.TryGetProperty("inEffect", out var x) && x.ValueKind == JsonValueKind.True))],
        [.. json.GetProperty("recipients").EnumerateArray().Select(e => new AudienceRecipientInfo(
            e.GetProperty("userId").GetGuid(), e.GetProperty("displayName").GetString() ?? string.Empty,
            e.GetProperty("isManager").GetBoolean(),
            [.. e.GetProperty("certificatePems").EnumerateArray().Select(c => c.GetString() ?? string.Empty)]))],
        [.. json.GetProperty("candidates").EnumerateArray().Select(e => new AudienceCandidateInfo(
            OptionalGuid(e, "userId"), OptionalGuid(e, "groupId"), e.GetProperty("displayName").GetString() ?? string.Empty))],
        LinkMap.From(json) ?? LinkMap.Empty);

    private static Guid? OptionalGuid(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetGuid() : null;
}

/// <summary>Somebody changed the audience since it was read: reload it and decide again (a 412).</summary>
public sealed class AudienceChangedException()
    : ApiActionException("The audience was changed by somebody else since you opened it — reload it and try again.");

/// <summary>
/// Reads and saves the audience of encrypted content (ADR 0983), by the <c>audience</c> rel a store or a document
/// advertises. UI-free and key-free: saving declares who is MEANT to decrypt; re-sealing to it is the caller's next
/// step (<c>PasswordStoreClient.ChangeAudienceAsync</c>, <c>DocumentsClient.ChangeSealedAudienceAsync</c>).
/// </summary>
public sealed class ContentAudienceClient(ApiCore core)
{
    /// <summary>The audience, by the <c>audience</c> rel of a store, a folder or entry in one, or a Sealed document.</summary>
    public async Task<ContentAudienceInfo> GetAsync(LinkMap owner, CancellationToken cancellationToken = default) =>
        await GetAsync(owner.Href("audience") ?? throw new InvalidOperationException("No 'audience' rel was advertised (ADR 0543)."), cancellationToken);

    /// <summary>The audience at an href an earlier read advertised.</summary>
    public async Task<ContentAudienceInfo> GetAsync(string href, CancellationToken cancellationToken = default)
    {
        using var response = await core.Http.GetAsync(href, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The audience could not be read.", cancellationToken);
        return ContentAudienceInfo.From(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    /// <summary>
    /// Replaces the whole declared audience under the tag of the read that filled the editor (ADR 0794): one request,
    /// one precondition. Answers the audience as it is now, with the declaring document's new tag.
    /// </summary>
    /// <exception cref="AudienceChangedException">Somebody changed it meanwhile (412).</exception>
    public async Task<ContentAudienceInfo> SaveAsync(
        ContentAudienceInfo seen, IReadOnlyList<AudienceChange> members, CancellationToken cancellationToken = default)
    {
        // `self` serves GET and PUT (ADR 0719); whether this caller may PUT is the resource's capability flags.
        var href = seen.Links.Href("self") ?? throw new InvalidOperationException("The audience advertised no 'self' rel (ADR 0543).");
        using var request = new HttpRequestMessage(HttpMethod.Put, href)
        {
            Content = JsonContent.Create(new { members = members.Select(m => new { userId = m.UserId, groupId = m.GroupId, isManager = m.IsManager }) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", seen.ETag);
        using var response = await core.Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new AudienceChangedException();
        }

        await ApiCore.ThrowIfProblemAsync(response, "The audience could not be saved.", cancellationToken);
        return ContentAudienceInfo.From(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), response.Headers.ETag?.Tag);
    }
}
