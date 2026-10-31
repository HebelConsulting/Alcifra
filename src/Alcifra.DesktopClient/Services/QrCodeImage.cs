using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// Reads the QR code in an image (ADR 1013): the desktop has no camera to point at a site's verification-code QR, so the
/// user takes a screenshot of it with the system's own tool and pastes it, or picks a saved image. The whole screenshot
/// is searched, so the QR need not be cropped.
/// </summary>
public static class QrCodeImage
{
    /// <summary>The formats a clipboard may hold a screenshot in, preferred first (the macOS, Linux and Windows names).</summary>
    public static readonly IReadOnlyList<string> ClipboardFormats = ["public.png", "image/png", "PNG", "public.jpeg", "image/jpeg", "JFIF"];

    /// <summary>The text of the first QR code in <paramref name="image"/> (PNG, JPEG, BMP, WebP, GIF), or null when the
    /// image does not decode or holds none.</summary>
    public static string? Decode(byte[] image)
    {
        using var decoded = SKBitmap.Decode(image);
        if (decoded is null)
        {
            return null;
        }

        using var bgra = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
        var pixels = (bgra ?? decoded).Bytes;
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true },
        };
        var source = new RGBLuminanceSource(pixels, decoded.Width, decoded.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        return reader.Decode(source)?.Text;
    }
}
