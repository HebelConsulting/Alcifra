using System.Text;

namespace Alcifra.Theming;

/// <summary>
/// What a runtime brand pack's asset actually is. The server serves a pack's files under the asset's WELL-KNOWN KEY
/// (<c>/api/brand/assets/wordmarkLight</c>, <see cref="BrandPackAssets"/>), never under its file name, so the address
/// says nothing about the format — a pack may declare <c>mark.svg</c> or <c>mark.png</c> behind the same key. The
/// served content type decides; the bytes decide when the server serves none worth the name.
/// </summary>
/// <remarks>
/// Shared so the two native clients decide the same way: the desktop picked its loader from the cached FILE NAME once,
/// which has no extension for a key, and drew a PNG decoder over the Bund's SVG (2026-11-23);
/// <c>Alcifra.Mobile.Core.Branding.BrandAsset</c> reached the same rule from the other side (Mobile #320).
/// </remarks>
public static class BrandAssets
{
    /// <summary>Whether the asset is an SVG, which needs an SVG renderer rather than a raster decoder.</summary>
    public static bool IsSvg(byte[] bytes, string? contentType)
    {
        if (string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A server that served application/octet-stream: an SVG document is XML, so it opens with a declaration,
        // a comment or the root element, after any byte-order mark and whitespace.
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        head = head.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!--", StringComparison.Ordinal);
    }
}
