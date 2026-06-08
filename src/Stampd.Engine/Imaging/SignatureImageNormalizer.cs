using SkiaSharp;

namespace Stampd.Engine.Imaging;

/// <summary>
/// Normalizes signature image bytes so PDFsharp's <see cref="PdfSharp.Drawing.XImage"/>
/// reader handles transparent PNGs correctly. v2.1 #213.
/// </summary>
/// <remarks>
/// <para>
/// The Problem (v2.0): browser canvas PNGs (the kind that come out of
/// <c>HTMLCanvasElement.toDataURL('image/png')</c>) carry an 8-bit RGBA alpha
/// channel. PDFsharp 6.x's PNG path renders alpha=0 pixels as opaque
/// (R=0, G=0, B=0) black on the output PDF — manifesting as a black rectangle
/// behind the signature ink. The customer-facing bring-your-own-cert demo
/// surfaced this immediately.
/// </para>
/// <para>
/// The Fix (v2.1): decode every incoming signature image via SkiaSharp, then
/// re-emit it via Skia's PNG encoder. Skia produces a strict, well-formed PNG
/// with a clean colour-type-6 (RGBA) header that PDFsharp's reader handles
/// correctly. We don't fight PDFsharp's image pipeline — we just hand it a
/// PNG it parses cleanly.
/// </para>
/// <para>
/// For non-PNG inputs (JPEG, BMP, etc.) we pass the bytes through unchanged.
/// JPEG has no alpha to worry about; BMP is rare enough that adopters using it
/// can open an issue if they need it. Stampd's signer flows all emit PNG.
/// </para>
/// </remarks>
internal static class SignatureImageNormalizer
{
    /// <summary>
    /// First 8 bytes of every PNG file, per ISO 15948 / RFC 2083 §3.1.
    /// </summary>
    private static readonly byte[] PngMagic = new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    };

    /// <summary>
    /// Re-encode a PNG via SkiaSharp so PDFsharp's reader sees a clean RGBA file.
    /// Non-PNG inputs are returned unchanged.
    /// </summary>
    /// <param name="input">Raw signature image bytes as provided by the API caller.</param>
    /// <returns>
    /// A byte array suitable for <c>XImage.FromStream(new MemoryStream(bytes))</c>.
    /// When the input was a PNG, the output is a re-encoded PNG with a normalized
    /// header + alpha channel; otherwise the output is the input copied into a new
    /// array.
    /// </returns>
    public static byte[] Normalize(ReadOnlySpan<byte> input)
    {
        if (!IsPng(input))
        {
            // Not a PNG — pass through. We still copy because the caller's span
            // backing buffer may be reused after the engine returns; PDFsharp
            // holds onto the bytes asynchronously.
            return input.ToArray();
        }

        // SkiaSharp's decode accepts only byte[], not ReadOnlySpan<byte>.
        // The copy is unavoidable here.
        var inputArray = input.ToArray();
        using var skBitmap = SKBitmap.Decode(inputArray);
        if (skBitmap is null)
        {
            // Skia couldn't decode it; trust PDFsharp to refuse the input gracefully.
            return inputArray;
        }

        // Re-encode at full quality (PNG is lossless; the quality knob is a
        // no-op but the API requires a value). The resulting PNG has a
        // canonical IHDR (color type 6 = truecolour with alpha) + an explicit
        // alpha channel.
        using var skImage = SKImage.FromBitmap(skBitmap);
        using var skData = skImage.Encode(SKEncodedImageFormat.Png, quality: 100);

        return skData.ToArray();
    }

    /// <summary>
    /// True if <paramref name="bytes"/> starts with the 8-byte PNG magic signature.
    /// </summary>
    private static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PngMagic.Length) return false;
        for (var i = 0; i < PngMagic.Length; i++)
        {
            if (bytes[i] != PngMagic[i]) return false;
        }
        return true;
    }
}
