using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Documents;
using Alcifra.Api.Hypermedia;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Controllers;

/// <summary>
/// A mail as the preview's card (#2215, ADR 1073): the envelope, the body as sanitised HTML and as text, its inline images,
/// and the attachments the finalizer filed as its children. Read-only: a mail is not edited.
/// </summary>
/// <remarks>
/// An inline image is served here from inside the mail, decoded, as a contact's photo is from inside its card: it is not a
/// stored object, so there is nothing to presign. Its content type is sniffed from the bytes, never taken from the mail, so a
/// sender cannot choose what this origin serves.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/email-card")]
[Authorize]
public class DocumentEmailCardController(
    AlcifraDbContext dbContext, DocumentAccessService access, IObjectStorageClient storage, IEmailCardReader reader) : ControllerBase
{
    public class EmailCardResource : HypermediaResource
    {
        public string Subject { get; set; } = string.Empty;

        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;

        public string? Cc { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? Date { get; set; }

        /// <summary>The body as sanitised HTML, its inline images at this card's own addresses; null where it has none.</summary>
        public string? Html { get; set; }

        /// <summary>The body as text, for a client that does not show HTML.</summary>
        public string Text { get; set; } = string.Empty;

        public List<EmailAttachmentResource> Attachments { get; set; } = [];
    }

    /// <summary>An attachment, filed as a child document of the mail when it arrived; its rels open and download it.</summary>
    public class EmailAttachmentResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken) => await ReadAsync(documentId, body: true, cancellationToken);

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken) => await ReadAsync(documentId, body: false, cancellationToken);

    /// <summary>One inline image, by its index in the card.</summary>
    [HttpGet("inline/{index:int}")]
    public async Task<IActionResult> Inline(Guid documentId, int index, CancellationToken cancellationToken) =>
        await InlineAsync(documentId, index, body: true, cancellationToken);

    [HttpHead("inline/{index:int}")]
    public async Task<IActionResult> HeadInline(Guid documentId, int index, CancellationToken cancellationToken) =>
        await InlineAsync(documentId, index, body: false, cancellationToken);

    private async Task<IActionResult> ReadAsync(Guid documentId, bool body, CancellationToken cancellationToken)
    {
        if (!(await access.GetCallerRightsAsync(documentId, cancellationToken)).CanReadContent)
        {
            return Forbid();
        }

        if (await ResolveAsync(documentId, cancellationToken) is not { } card)
        {
            return NotFound();
        }

        var resource = new EmailCardResource
        {
            Subject = card.Subject,
            From = card.From,
            To = card.To,
            Cc = card.Cc,
            Date = card.Date,
            Html = card.Html is null ? null : InlineAddresses(card.Html, documentId),
            Text = card.Text,
            Links = [new Link("self", $"/api/documents/{documentId}/email-card", "GET"), new Link("document", $"/api/documents/{documentId}", "GET")],
        };

        // Each inline image is advertised as well as named in the HTML: the HTML is a rendering, the rels are the API, and
        // a client that never parses the body still finds them (ADR 0543).
        for (var index = 0; index < card.Inline.Count; index++)
        {
            resource.Links.Add(new Link("inline-image", InlineHref(documentId, index), "GET"));
        }

        // The attachments the caller may see, in the order the mail carried them as far as the archive knows it (by name).
        var children = await dbContext.Documents.Where(d => d.ParentId == documentId).OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name }).ToListAsync(cancellationToken);
        var rights = await access.GetCallerRightsForManyAsync([.. children.Select(c => c.Id)], cancellationToken);
        resource.Attachments = [.. children.Where(c => rights.TryGetValue(c.Id, out var r) && r.CanSee).Select(c => new EmailAttachmentResource
        {
            Id = c.Id,
            Name = c.Name,
            Links = [new Link("self", $"/api/documents/{c.Id}", "GET"), new Link("versions", $"/api/documents/{c.Id}/versions", "GET")],
        })];

        return body ? Ok(resource) : new EmptyResult();
    }

    private async Task<IActionResult> InlineAsync(Guid documentId, int index, bool body, CancellationToken cancellationToken)
    {
        if (!(await access.GetCallerRightsAsync(documentId, cancellationToken)).CanReadContent)
        {
            return Forbid();
        }

        if (await ResolveAsync(documentId, cancellationToken) is not { } card || index < 0 || index >= card.Inline.Count
            || ImageType(card.Inline[index].Bytes) is not { } contentType)
        {
            return NotFound();
        }

        var bytes = card.Inline[index].Bytes;
        Response.Headers.CacheControl = "private, max-age=300";
        if (!body)
        {
            Response.ContentType = contentType;
            Response.ContentLength = bytes.Length;
            return new EmptyResult();
        }

        return File(bytes, contentType);
    }

    // The mail behind a document that wears the eMail mask, read through the content door the client's own reads use.
    private async Task<EmailCard?> ResolveAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null || !await dbContext.MaskVersions.AnyAsync(v => v.Id == document.MaskVersionId && v.MaskId == WellKnownMaskIds.EMail, cancellationToken))
        {
            return null;
        }

        var version = await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        var extension = Path.GetExtension(version?.ObjectKey ?? string.Empty);
        if (version is null || extension.ToLowerInvariant() is not (".eml" or ".msg"))
        {
            return null;
        }

        await using var stream = await storage.GetObjectForClientAsync(version.ObjectKey, "the mail view", cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return reader.Read(buffer.ToArray(), extension);
    }

    // The sanitised HTML names an inline image as cid:{index}; the client reaches it at this card's own address.
    private static string InlineAddresses(string html, Guid documentId) =>
        System.Text.RegularExpressions.Regex.Replace(html, "src=\"cid:(\\d+)\"", m => $"src=\"{InlineHref(documentId, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))}\"");

    private static string InlineHref(Guid documentId, int index) => $"/api/documents/{documentId}/email-card/inline/{index}";

    // Only the image formats a browser shows, recognised by their bytes.
    private static string? ImageType(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };
}
