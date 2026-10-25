using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;
using SimplArchive.Messenger.Client;

namespace SimplArchive.ApiClient.PasswordStore;

/// <summary>
/// The password store, UI-free, for the phone now and the desktop and <c>sapass</c> later (ADR 0964): browse by the rels
/// the server advertises, open an entry with the host's key, and seal every change on the device to the store's readers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server never sees a secret.</b> An entry's content is a CMS envelope sealed here, by the one sealer the Messenger
/// and the offline archive use (<see cref="ConversationKeys.SealTo"/>, RSA and elliptic-curve recipients alike), to every
/// certificate the store's <c>password-store-recipients</c> names; it is opened by the host's
/// <see cref="IEnvelopeOpener"/>, the same one a strict-tier document is opened by.
/// </para>
/// <para>
/// <b>Every change carries what the user saw</b> (ADR 0794): a new version is finalized under the entry's ETag from the
/// read that filled the form, and a 412 is <see cref="PasswordEntryChangedException"/>. A re-seal does the same per entry,
/// and skips an entry a 412 says somebody changed meanwhile.
/// </para>
/// </remarks>
public sealed class PasswordStoreClient(ApiCore core, IEnvelopeOpener? opener = null)
{
    private const string SealedExtension = ".p7m";
    private const string EnvelopeType = "application/pkcs7-mime; smime-type=enveloped-data";

    private IEnvelopeOpener Opener => opener ?? ApiClientSettings.EnvelopeOpener;

    /// <summary>The caller's stores, My Passwords first, and whether My Passwords may be created (it is, on first use).</summary>
    public async Task<(IReadOnlyList<PasswordStoreInfo> Stores, bool OffersMyPasswords)> ListStoresAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(await core.RootHrefAsync("passwordStores", cancellationToken), cancellationToken);
        var stores = json.GetProperty("stores").EnumerateArray()
            .Select(s => new PasswordStoreInfo(
                s.GetProperty("id").GetGuid(),
                s.GetProperty("name").GetString() ?? string.Empty,
                s.GetProperty("isPersonal").GetBoolean(),
                ApiCore.RequireRel(s, "self", "A password store")))
            .ToList();
        return (stores, ApiCore.RelHref(json, "my-passwords") is not null);
    }

    /// <summary>The caller's My Passwords, created the first time (idempotent).</summary>
    public async Task<PasswordFolderInfo> EnsureMyPasswordsAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetAsync(await core.RootHrefAsync("passwordStores", cancellationToken), cancellationToken);
        using var response = await core.SendRelAsync(list, "my-passwords", cancellationToken: cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "My Passwords could not be opened.", cancellationToken);
        return Folder(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    /// <summary>A store or folder, by the href a store or a row advertised.</summary>
    public async Task<PasswordFolderInfo> GetFolderAsync(string href, CancellationToken cancellationToken = default) =>
        Folder(await GetAsync(href, cancellationToken));

    /// <summary>
    /// One page of the store's flat descendants listing (ADR 0207), and the href of the next page — the whole tree in
    /// a few requests, for a client that completes or searches over it rather than browsing folder by folder.
    /// </summary>
    /// <param name="href">The <c>descendants</c> rel a store or folder advertised.</param>
    public async Task<(IReadOnlyList<PasswordDescendantInfo> Items, string? Next)> DescendantsPageAsync(
        string href, CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(href, cancellationToken);
        var items = json.GetProperty("items").EnumerateArray()
            .Select(i => new PasswordDescendantInfo(
                i.GetProperty("id").GetGuid(),
                i.GetProperty("name").GetString() ?? string.Empty,
                i.GetProperty("kind").GetString() == "folder",
                i.GetProperty("parentId").GetGuid(),
                ApiCore.RequireRel(i, "self", "A descendant row"),
                ApiWire.StrOrNull(i, "site"),
                ApiWire.StrOrNull(i, "userName"),
                ApiWire.StrOrNull(i, "app")))
            .ToList();
        var next = json.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == "next")
            .Select(l => l.GetProperty("href").GetString())
            .FirstOrDefault();
        return (items, next);
    }

    /// <summary>
    /// EVERY descendant of a store or folder, following the listing's <c>next</c> rels — the whole tree in one call
    /// for a client that completes, searches or matches over it (ADR 0207; the paging stays the server's).
    /// </summary>
    /// <param name="href">The <c>descendants</c> rel a store or folder advertised.</param>
    public async Task<IReadOnlyList<PasswordDescendantInfo>> AllDescendantsAsync(
        string href, CancellationToken cancellationToken = default)
    {
        var all = new List<PasswordDescendantInfo>();
        string? next = href;
        while (next is not null)
        {
            var (page, following) = await DescendantsPageAsync(next, cancellationToken);
            all.AddRange(page);
            next = following;
        }

        return all;
    }

    /// <summary>An entry's resource, by the href its row advertised; its ETag is what the next change sends.</summary>
    public async Task<PasswordEntryInfo> GetEntryAsync(string href, CancellationToken cancellationToken = default) =>
        PasswordEntryInfo.From(await GetAsync(href, cancellationToken));

    /// <summary>Opens an entry with the host's key: the sealed bytes through the content funnel, then the opener.</summary>
    /// <exception cref="EnvelopeNotOpenedException">No key at hand opens it (none present, or it is not sealed to this reader).</exception>
    public async Task<OpenedPasswordEntry> OpenAsync(PasswordEntryInfo entry, CancellationToken cancellationToken = default)
    {
        var href = entry.Links.Href("content")
            ?? throw new InvalidOperationException($"'{entry.Name}' has no content this reader may fetch (no 'content' rel, ADR 0543).");
        var sealedBytes = await ApiCore.GetContentBytesAsync(href, cancellationToken);
        var (opened, _) = await Opener.OpenAsync(AsSmime(sealedBytes), EnvelopeType);
        ApiClientSettings.Logger.LogDebug("Opened password entry {EntryId}", entry.Id);
        return new OpenedPasswordEntry(entry, PasswordEntryFormat.Parse(opened));
    }

    /// <summary>Who a store's entries must be sealed to, from any store, folder or entry of it.</summary>
    public async Task<PasswordStoreRecipients> GetRecipientsAsync(LinkMap links, CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(links.Href("password-store-recipients")
            ?? throw new InvalidOperationException("No 'password-store-recipients' rel was advertised (ADR 0543)."), cancellationToken);
        return new PasswordStoreRecipients(
            [.. json.GetProperty("certificates").EnumerateArray().Select(c => new PasswordStoreRecipientInfo(
                c.GetProperty("userId").GetGuid(), c.GetProperty("displayName").GetString() ?? string.Empty, c.GetProperty("certificatePem").GetString()!))],
            [.. json.GetProperty("readersWithoutCertificate").EnumerateArray().Select(r => (r.GetProperty("userId").GetGuid(), r.GetProperty("displayName").GetString() ?? string.Empty))]);
    }

    /// <summary>A new entry in <paramref name="folder"/>, sealed to the store's readers.</summary>
    public async Task<PasswordEntryInfo> CreateEntryAsync(PasswordFolderInfo folder, string name, PasswordEntryContent content, CancellationToken cancellationToken = default)
    {
        var sealedBytes = await SealAsync(folder.Links, content, cancellationToken);
        using var response = await core.SendRelAsync(folder.Links, "password-entries", new { name }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentNameTakenException(name);
        }

        await ApiCore.ThrowIfProblemAsync(response, $"'{name}' could not be created.", cancellationToken);
        var created = PasswordEntryInfo.From(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
        await UploadVersionAsync(created, sealedBytes, content, ifMatch: null, commentKind: null, passwordChanged: null, cancellationToken);
        return await GetEntryAsync(created.Links.Href("self")!, cancellationToken);
    }

    /// <summary>
    /// Files the FIRST content of an entry whose create was interrupted before its upload finished (it has no content,
    /// and the history sweep removes it after a day, ADR 0964), sealed to the store's readers. A re-run of a script
    /// completes what an earlier run began instead of leaving an empty entry behind (ADR 0966).
    /// </summary>
    public async Task<PasswordEntryInfo> CompleteEntryAsync(PasswordEntryInfo entry, PasswordEntryContent content, CancellationToken cancellationToken = default)
    {
        if (entry.HasContent)
        {
            throw new InvalidOperationException($"'{entry.Name}' already has content; a change is a new version (SaveEntryAsync).");
        }

        var sealedBytes = await SealAsync(entry.Links, content, cancellationToken);
        await UploadVersionAsync(entry, sealedBytes, content, ifMatch: null, commentKind: null, passwordChanged: null, cancellationToken);
        return await GetEntryAsync(entry.Links.Href("self")!, cancellationToken);
    }

    /// <summary>
    /// Saves an edit as a new version under the ETag of the read that filled the form. A changed password is said, which
    /// is the one change that clears "Exposed since".
    /// </summary>
    /// <exception cref="PasswordEntryChangedException">Somebody changed the entry since it was opened.</exception>
    public async Task<PasswordEntryInfo> SaveEntryAsync(OpenedPasswordEntry opened, PasswordEntryContent edited, CancellationToken cancellationToken = default)
    {
        var sealedBytes = await SealAsync(opened.Entry.Links, edited, cancellationToken);
        var changed = !string.Equals(opened.Content.Password, edited.Password, StringComparison.Ordinal);
        await UploadVersionAsync(opened.Entry, sealedBytes, edited, opened.Entry.ETag, commentKind: null, passwordChanged: changed, cancellationToken);
        return await GetEntryAsync(opened.Entry.Links.Href("self")!, cancellationToken);
    }

    /// <summary>A new folder inside <paramref name="folder"/>.</summary>
    public async Task<PasswordFolderInfo> CreateFolderAsync(PasswordFolderInfo folder, string name, CancellationToken cancellationToken = default)
    {
        using var response = await core.SendRelAsync(folder.Links, "password-folders", new { name }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DocumentNameTakenException(name);
        }

        await ApiCore.ThrowIfProblemAsync(response, $"'{name}' could not be created.", cancellationToken);
        return Folder(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken));
    }

    /// <summary>Renames a folder or entry (the name is in the clear), under its ETag.</summary>
    public Task RenameAsync(LinkMap links, string currentName, string eTag, string newName, CancellationToken cancellationToken = default) =>
        SendWithTagAsync(HttpMethod.Put, Require(links, "document", currentName), eTag, new { name = newName }, currentName, cancellationToken);

    /// <summary>Moves a folder or entry into another folder of a store, under its ETag.</summary>
    public Task MoveAsync(LinkMap links, string name, string eTag, PasswordFolderInfo target, CancellationToken cancellationToken = default) =>
        SendWithTagAsync(HttpMethod.Put, Require(links, "move", name), eTag, new { parentId = target.Id }, name, cancellationToken);

    /// <summary>Deletes a folder or entry (to the recycle bin, as any document), under its ETag.</summary>
    public Task DeleteAsync(LinkMap links, string name, string eTag, CancellationToken cancellationToken = default) =>
        SendWithTagAsync(HttpMethod.Delete, Require(links, "document", name), eTag, null, name, cancellationToken);

    /// <summary>
    /// Re-seals every entry the store lists as needing it (ADR 0964): each opened with the host's key (one PIN, held by
    /// the host for the session where it may), sealed to the current readers, and filed as a system version under its
    /// own ETag. An entry somebody changed meanwhile (412) is skipped; one this key cannot open is reported.
    /// </summary>
    public async Task<PasswordResealOutcome> ResealAsync(PasswordFolderInfo store, CancellationToken cancellationToken = default)
    {
        var list = await GetAsync(Require(store.Links, "reseal", store.Name), cancellationToken);
        var recipients = Certificates(await GetRecipientsAsync(store.Links, cancellationToken));
        var resealed = new List<string>();
        var skipped = new List<string>();
        var failed = new List<(string, string)>();
        foreach (var entry in list.GetProperty("entries").EnumerateArray().Select(PasswordEntryInfo.From))
        {
            try
            {
                var opened = await OpenAsync(entry, cancellationToken);
                var sealedBytes = ConversationKeys.SealTo(PasswordEntryFormat.ToBytes(opened.Content), recipients);
                await UploadVersionAsync(entry, sealedBytes, opened.Content, entry.ETag, commentKind: ResealedAfterAccessChange, passwordChanged: false, cancellationToken);
                resealed.Add(entry.Name);
            }
            catch (PasswordEntryChangedException)
            {
                skipped.Add(entry.Name);
            }
            catch (Exception e) when (e is EnvelopeNotOpenedException or PasswordEntryUnreadableException or InvalidOperationException)
            {
                failed.Add((entry.Name, e.Message));
            }
        }

        ApiClientSettings.Logger.LogInformation("Re-sealed password store {StoreId}: {Resealed} entr(ies), {Skipped} changed meanwhile, {Failed} not opened",
            store.StoreId, resealed.Count, skipped.Count, failed.Count);
        return new PasswordResealOutcome(resealed, skipped, failed);
    }

    /// <summary>Carries out an import plan (<see cref="PasswordImport.Plan"/>) in <paramref name="folder"/>, sealing each
    /// row on this device; answers the names filed.</summary>
    public async Task<IReadOnlyList<string>> ImportAsync(PasswordFolderInfo folder, IReadOnlyList<PasswordImportStep> steps, CancellationToken cancellationToken = default)
    {
        var filed = new List<string>();
        foreach (var step in steps)
        {
            if (step.NewVersionOf is { } existing)
            {
                var entry = await GetEntryAsync(existing.Href, cancellationToken);
                var sealedBytes = await SealAsync(entry.Links, step.Draft.Content, cancellationToken);
                await UploadVersionAsync(entry, sealedBytes, step.Draft.Content, entry.ETag, commentKind: null, passwordChanged: true, cancellationToken);
            }
            else
            {
                await CreateEntryAsync(folder, step.Draft.Name, step.Draft.Content, cancellationToken);
            }

            filed.Add(step.Draft.Name);
        }

        return filed;
    }

    /// <summary>The bare CMS envelope of <paramref name="content"/>, sealed to every given certificate.</summary>
    public static byte[] Seal(PasswordEntryContent content, IReadOnlyList<X509Certificate2> recipients) =>
        recipients.Count == 0
            ? throw new PasswordStoreHasNoRecipientsException()
            : ConversationKeys.SealTo(PasswordEntryFormat.ToBytes(content), recipients);

    /// <summary>The bare CMS envelope as the S/MIME message an <see cref="IEnvelopeOpener"/> expects.</summary>
    public static byte[] AsSmime(byte[] cms)
    {
        var message = new MimeMessage { Body = new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(cms)) };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    // VersionCommentKind.ResealedAfterAccessChange on the wire (ADR 0964).
    private const int ResealedAfterAccessChange = 2;

    private async Task<byte[]> SealAsync(LinkMap links, PasswordEntryContent content, CancellationToken cancellationToken) =>
        Seal(content, Certificates(await GetRecipientsAsync(links, cancellationToken)));

    private static List<X509Certificate2> Certificates(PasswordStoreRecipients recipients) =>
        [.. recipients.Certificates.Select(c => X509Certificate2.CreateFromPem(c.CertificatePem))];

    // POST the entry's versions, PUT the sealed bytes to the presigned address (encrypted first where the tenant says
    // so, ADR 0818), PUT the version under the entry's ETag. One user action, one precondition (ADR 0794).
    private async Task UploadVersionAsync(PasswordEntryInfo entry, byte[] sealedBytes, PasswordEntryContent content, string? ifMatch, int? commentKind, bool? passwordChanged, CancellationToken cancellationToken)
    {
        using var created = await core.SendRelAsync(entry.Links, "versions", new { fileExtension = SealedExtension }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(created, $"'{entry.Name}' could not be saved.", cancellationToken);
        var version = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var encrypted = UploadEncryption.EncryptIfInstructed(version, sealedBytes);
        using (var upload = new ByteArrayContent(encrypted?.Blob ?? sealedBytes))
        {
            upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await ApiCore.Anonymous.PutAsync(version.GetProperty("uploadUrl").GetString(), upload, cancellationToken);
            put.EnsureSuccessStatusCode();
        }

        using var finalize = new HttpRequestMessage(HttpMethod.Put, ApiCore.RequireRel(version, "self", "The pending version"))
        {
            // The autofill index rides with the version it describes (ADR 0973): the client derived it from the content
            // it just sealed, so the entry is matchable without opening it. The server never sees the content.
            Content = JsonContent.Create(new
            {
                wrappedDek = encrypted?.WrappedDek,
                kekGeneration = encrypted?.KekGeneration,
                commentKind,
                passwordChanged,
                index = new { site = content.Site, userName = content.UserName, app = content.App },
            }),
        };
        if (ifMatch is not null)
        {
            finalize.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var finalized = await core.Http.SendAsync(finalize, cancellationToken);
        if (finalized.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new PasswordEntryChangedException(entry.Name);
        }

        await ApiCore.ThrowIfProblemAsync(finalized, $"'{entry.Name}' could not be saved.", cancellationToken);
    }

    private async Task SendWithTagAsync(HttpMethod method, string href, string eTag, object? body, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, href);
        request.Headers.TryAddWithoutValidation("If-Match", eTag);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await core.Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new PasswordEntryChangedException(name);
        }

        await ApiCore.ThrowIfProblemAsync(response, $"'{name}' could not be changed.", cancellationToken);
    }

    private async Task<JsonElement> GetAsync(string href, CancellationToken cancellationToken)
    {
        using var response = await core.Http.GetAsync(href, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "The password store could not be read.", cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json;
    }

    private static string Require(LinkMap links, string rel, string name) =>
        links.Href(rel) ?? throw new InvalidOperationException($"'{name}' advertised no '{rel}' rel (ADR 0543).");

    private static PasswordFolderInfo Folder(JsonElement json) => new(
        json.GetProperty("id").GetGuid(),
        json.GetProperty("name").GetString() ?? string.Empty,
        json.GetProperty("kind").GetString() == "store",
        json.GetProperty("storeId").GetGuid(),
        json.GetProperty("isPersonal").GetBoolean(),
        json.GetProperty("needsReseal").GetBoolean(),
        json.GetProperty("urgentReseal").GetBoolean(),
        json.GetProperty("readerCertificateCount").GetInt32(),
        json.TryGetProperty("eTag", out var tag) ? tag.GetString() ?? string.Empty : string.Empty,
        [
            .. json.GetProperty("items").EnumerateArray().Select(i => new PasswordItemInfo(
                i.GetProperty("id").GetGuid(),
                i.GetProperty("name").GetString() ?? string.Empty,
                i.GetProperty("kind").GetString() == "folder",
                i.GetProperty("eTag").GetString() ?? string.Empty,
                i.GetProperty("canEdit").GetBoolean(),
                i.GetProperty("canRename").GetBoolean(),
                i.GetProperty("canDelete").GetBoolean(),
                i.GetProperty("canMove").GetBoolean(),
                LinkMap.From(i) ?? LinkMap.Empty,
                ApiWire.StrOrNull(i, "site"),
                ApiWire.StrOrNull(i, "userName"),
                ApiWire.StrOrNull(i, "app"))),
        ],
        LinkMap.From(json) ?? LinkMap.Empty);
}
