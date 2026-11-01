using System.Globalization;
using System.Text;
using Alcifra.Domain.Users;
using Alcifra.Presentation;

namespace Alcifra.Api.WebDav;

/// <summary>
/// The one file at the WebDAV root that is not part of the Repositories tree (#2053): <c>.alcifra-account</c>, naming
/// whose mount it is — the signed-in account's address and its tenant's id, one per line. Every user and every tenant of
/// an installation mount the same address, and an operating system lists a mounted volume by that address alone, so this
/// is how a client tells its own volume from another user's before opening it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A deliberate exception to "WebDAV mirrors the Repositories tree"</b> (ADR 0509, narrowed by ADR 1023): it is not a
/// document and carries nothing a person files. Hidden by its leading dot (Finder, file managers on Linux) and by the
/// Windows attribute <c>Win32FileAttributes</c> = hidden (Explorer); read-only — every write to it is refused.
/// </para>
/// <para>Computed per request for whoever signed in; nothing is stored, and it carries no secret.</para>
/// </remarks>
internal static class WebDavAccountFile
{
    // FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_HIDDEN, as Windows' WebDAV redirector reads them.
    private const string HiddenReadOnly = "00000003";

    /// <summary>Whether the request addresses the account file: the root's one entry by that name.</summary>
    internal static bool Is(IReadOnlyList<string> segments) => segments is [var only] && only == WireNames.WebDavAccountFile;

    internal static byte[] Content(User user) =>
        Encoding.UTF8.GetBytes($"{user.Email}\n{user.TenantId.ToString("D", CultureInfo.InvariantCulture)}\n");

    /// <summary>Its entry in a listing of the root, or its own PROPFIND answer.</summary>
    internal static PropStatXml Prop(string basePath, User user)
    {
        var entry = WebDavReads.FileProp(basePath, [WireNames.WebDavAccountFile], Content(user).Length, user.CreatedAt, "text/plain; charset=utf-8");
        return entry with
        {
            Props = entry.Props
                + $"<Z:Win32FileAttributes xmlns:Z=\"urn:schemas-microsoft-com:\">{HiddenReadOnly}</Z:Win32FileAttributes>",
        };
    }

    /// <summary>Answers every verb addressed to the file: reads are served, every write is refused.</summary>
    internal static async Task HandleAsync(HttpContext context, string method, User user, string basePath)
    {
        switch (method)
        {
            case "PROPFIND":
                await WebDavXml.WriteMultiStatusAsync(context, [Prop(basePath, user)]);
                break;
            case "GET" or "HEAD":
                var content = Content(user);
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength = content.Length;
                context.Response.Headers.CacheControl = "no-store";   // whose mount it is must never be answered from a cache
                if (method == "GET")
                {
                    await context.Response.Body.WriteAsync(content, context.RequestAborted);
                }

                break;
            default:
                // PUT, DELETE, MOVE, COPY, LOCK, PROPPATCH, MKCOL: the file is the server's statement, not a document.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                break;
        }
    }
}
