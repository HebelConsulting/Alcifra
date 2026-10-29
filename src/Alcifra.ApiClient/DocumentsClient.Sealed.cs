using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.ApiClient;

/// <summary>
/// Filing a SEALED document (ADR 0975): the document wears the sealed mask, and its content is the CMS envelope the
/// caller sealed on this device — the server checks the bytes are shaped as an envelope and stores them untouched.
/// </summary>
/// <remarks>
/// A partial beside the rest of the document client rather than a call inside it: the two operations differ from an
/// ordinary upload in what the document WEARS and what the bytes ARE, and the version-upload sequence is the same one
/// the password store uses (create the pending version, PUT the envelope, finalize under the caller's precondition).
/// </remarks>
public sealed partial class DocumentsClient
{
    /// <summary>
    /// Files <paramref name="sealedBytes"/> as a new sealed document in the folder at <paramref name="childrenHref"/>,
    /// and answers its id.
    /// </summary>
    /// <param name="name">The document's name as the folder shows it (see "Name hidden" in ADR 0975).</param>
    public async Task<Guid> UploadSealedDocumentAsync(
        string childrenHref, string name, byte[] sealedBytes, CancellationToken cancellationToken = default)
    {
        using var createResponse = await _core.Http.PostAsJsonAsync(
            childrenHref, new { name, maskId = SealedDocument.MaskId }, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentNameTakenException(name);
        }

        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException($"'{name}': you don't have permission to upload here.");
        }

        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var documentId = created.GetProperty("id").GetGuid();

        using var versionResponse = await _core.Http.PostAsJsonAsync(
            ApiCore.RequireRel(created, "versions", "The created document"),
            new { fileExtension = SealedDocument.FileExtension }, cancellationToken);
        await VersionFailedAsync(versionResponse, name, cancellationToken);
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        await PutEnvelopeAsync(version, sealedBytes, cancellationToken);

        // The first version of a sealed document carries no precondition: there is no earlier state anybody saw.
        using var finalize = new HttpRequestMessage(HttpMethod.Put, ApiCore.RequireRel(version, "self", "The pending version"))
        {
            Content = JsonContent.Create(new { }),
        };
        using var finalized = await _core.Http.SendAsync(finalize, cancellationToken);
        await VersionFailedAsync(finalized, name, cancellationToken);
        return documentId;
    }

    /// <summary>
    /// Files a NEW version of a sealed document, sealed to a changed audience, under the document's ETag — the
    /// precondition of what its author saw (ADR 0794). The version is marked a re-seal, which is the system's own act
    /// (ADR 0964) and is audited as such.
    /// </summary>
    /// <param name="versionsHref">The document's <c>versions</c> rel, from the resource the pane loaded.</param>
    /// <param name="eTag">The DOCUMENT's ETag from that same read — never a re-read just before writing.</param>
    public async Task ResealAsync(
        string versionsHref, string eTag, string name, byte[] sealedBytes, CancellationToken cancellationToken = default)
    {
        using var versionResponse = await _core.Http.PostAsJsonAsync(
            versionsHref, new { fileExtension = SealedDocument.FileExtension }, cancellationToken);
        await VersionFailedAsync(versionResponse, name, cancellationToken);
        var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        await PutEnvelopeAsync(version, sealedBytes, cancellationToken);

        using var finalize = new HttpRequestMessage(HttpMethod.Put, ApiCore.RequireRel(version, "self", "The pending version"))
        {
            Content = JsonContent.Create(new { commentKind = SealedDocument.ResealedAfterAccessChange }),
        };
        finalize.Headers.TryAddWithoutValidation("If-Match", eTag);

        using var finalized = await _core.Http.SendAsync(finalize, cancellationToken);
        if (finalized.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ApiActionException("This document changed since you opened it — reload and try again.");
        }

        await VersionFailedAsync(finalized, name, cancellationToken);
    }

    /// <summary>
    /// Begins a new Sealed document (ADR 0983): creates it — the caller is its first manager — and reads its audience,
    /// so the people to add can be picked from the readers the server offers for it. Finish it with
    /// <see cref="CompleteSealedDocumentAsync"/>, or take it back out with <see cref="AbandonSealedDocumentAsync"/>.
    /// </summary>
    public async Task<SealedDocumentDraft> BeginSealedDocumentAsync(string childrenHref, string name, CancellationToken cancellationToken = default)
    {
        using var createResponse = await _core.Http.PostAsJsonAsync(childrenHref, new { name, maskId = SealedDocument.MaskId }, cancellationToken);
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentNameTakenException(name);
        }

        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiActionException($"'{name}': you don't have permission to upload here.");
        }

        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        using var read = await _core.Http.GetAsync(ApiCore.RequireRel(created, "self", "The created document"), cancellationToken);
        await ApiCore.ThrowIfProblemAsync(read, $"'{name}' could not be read back.", cancellationToken);
        var document = await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var draft = new SealedDocumentDraft(created.GetProperty("id").GetGuid(), name,
            ApiCore.RequireRel(created, "versions", "The created document"), ApiCore.RequireRel(created, "self", "The created document"),
            read.Headers.ETag?.Tag, null!);
        try
        {
            return draft with { Audience = await new ContentAudienceClient(_core).GetAsync(LinkMap.From(document) ?? LinkMap.Empty, cancellationToken) };
        }
        catch
        {
            await AbandonSealedDocumentAsync(draft, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Finishes a begun Sealed document: <paramref name="members"/> join its audience, and the content is sealed on this
    /// device to the audience's certificates as the server answers them — never to certificates picked by hand. On any
    /// failure the document is taken back out rather than left as a name with nothing behind it.
    /// </summary>
    /// <exception cref="SealedDocumentAudienceRequiredException">Nobody in the audience holds a certificate.</exception>
    public async Task<Guid> CompleteSealedDocumentAsync(
        SealedDocumentDraft draft, IReadOnlyList<AudienceChange> members, byte[] content, string contentType, string? fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var audience = draft.Audience;
            if (members.Count > 0)
            {
                audience = await new ContentAudienceClient(_core).SaveAsync(audience, members, cancellationToken);
                draft = draft with { ETag = audience.ETag };   // the save moved the document's tag
            }

            if (audience.Certificates() is not { Count: > 0 } certificates)
            {
                throw new SealedDocumentAudienceRequiredException();
            }

            var sealedBytes = SealedDocument.Seal(content, contentType, certificates, fileName);
            using var versionResponse = await _core.Http.PostAsJsonAsync(draft.VersionsHref, new { fileExtension = SealedDocument.FileExtension }, cancellationToken);
            await VersionFailedAsync(versionResponse, draft.Name, cancellationToken);
            var version = await versionResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await PutEnvelopeAsync(version, sealedBytes, cancellationToken);
            using var finalize = new HttpRequestMessage(HttpMethod.Put, ApiCore.RequireRel(version, "self", "The pending version")) { Content = JsonContent.Create(new { }) };
            using var finalized = await _core.Http.SendAsync(finalize, cancellationToken);
            await VersionFailedAsync(finalized, draft.Name, cancellationToken);
            return draft.Id;
        }
        catch
        {
            await AbandonSealedDocumentAsync(draft, cancellationToken);
            throw;
        }
    }

    /// <summary>Takes a begun Sealed document back out (the user cancelled, or its filing failed). Never throws.</summary>
    public async Task AbandonSealedDocumentAsync(SealedDocumentDraft draft, CancellationToken cancellationToken = default)
    {
        try
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, draft.SelfHref);
            if (draft.ETag is not null)
            {
                delete.Headers.TryAddWithoutValidation("If-Match", draft.ETag);
            }

            using var _ = await _core.Http.SendAsync(delete, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            ApiClientSettings.Logger.LogWarning(e, "A sealed document left without content could not be removed again");
        }
    }

    /// <summary>Files a new Sealed document in one call: begin, declare, seal, upload (what a script or a test needs).</summary>
    public async Task<Guid> CreateSealedDocumentAsync(
        string childrenHref, string name, byte[] content, string contentType, string? fileName,
        IReadOnlyList<AudienceCandidateInfo> members, CancellationToken cancellationToken = default)
    {
        var draft = await BeginSealedDocumentAsync(childrenHref, name, cancellationToken);
        var rows = members.Count == 0 ? [] : (IReadOnlyList<AudienceChange>)[.. draft.Audience.Members.Select(AudienceChange.Of), .. members.Select(AudienceChange.Of)];
        return await CompleteSealedDocumentAsync(draft, rows, content, contentType, fileName, cancellationToken);
    }

    /// <summary>
    /// Changes who a Sealed document is encrypted for, and re-seals it in the same act (ADR 0983): the current content is
    /// opened FIRST with the caller's key (a manager who cannot open it learns so before anything changes), then the
    /// audience is saved under the tag of the read that filled the editor, then the content is sealed to the audience as
    /// saved and filed as a re-seal under the document's new tag.
    /// </summary>
    /// <param name="versionsHref">The document's <c>versions</c> rel.</param>
    /// <param name="name">The document's name, carried inside the envelope.</param>
    public async Task<ContentAudienceInfo> ChangeSealedAudienceAsync(
        ContentAudienceInfo seen, IReadOnlyList<AudienceChange> members, string versionsHref, string name, CancellationToken cancellationToken = default)
    {
        var preview = await GetPreviewAsync(versionsHref, cancellationToken);
        var contentUrl = preview.DownloadUrl ?? preview.PreviewUrl
            ?? throw new ApiActionException($"'{name}' has no content you may open, so it cannot be sealed again.");
        var (bytes, contentType) = await ApiCore.GetContentAsync(contentUrl, cancellationToken);

        var saved = await new ContentAudienceClient(_core).SaveAsync(seen, members, cancellationToken);
        if (saved.Certificates() is not { Count: > 0 } certificates)
        {
            throw new SealedDocumentAudienceRequiredException();
        }

        await ResealAsync(versionsHref, saved.ETag, name, SealedDocument.Seal(bytes, contentType, certificates, name), cancellationToken);
        return saved;
    }

    /// <summary>Uploads the envelope to the pending version's presigned address, encrypting at rest where the server says so.</summary>
    private static async Task PutEnvelopeAsync(JsonElement version, byte[] sealedBytes, CancellationToken cancellationToken)
    {
        // At-rest encryption is the tenant's own layer BENEATH the seal (ADR 0818): the envelope is what the readers'
        // keys open, and the wrapped DEK is what this installation's storage opens. Both, where both apply.
        var encrypted = UploadEncryption.EncryptIfInstructed(version, sealedBytes);
        using var upload = new ByteArrayContent(encrypted?.Blob ?? sealedBytes);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var put = await ApiCore.Anonymous.PutAsync(version.GetProperty("uploadUrl").GetString()!, upload, cancellationToken);
        put.EnsureSuccessStatusCode();
    }

    /// <summary>The server's own refusal, or its problem document — a sealed filing that failed must say why.</summary>
    private static async Task VersionFailedAsync(HttpResponseMessage response, string name, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ApiCore.ThrowIfProblemAsync(response, $"'{name}' could not be filed.", cancellationToken);
        }

        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// A Sealed document created but not yet filed (ADR 0983): its addresses, its tag, and its audience — the caller as its
/// first manager, and the readers who may be added — for the editor to pick from.
/// </summary>
public sealed record SealedDocumentDraft(Guid Id, string Name, string VersionsHref, string SelfHref, string? ETag, ContentAudienceInfo Audience);
