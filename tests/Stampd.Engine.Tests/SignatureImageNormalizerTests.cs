using SkiaSharp;

using Stampd.Engine.Imaging;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Pins the v2.1 #213 behaviour: the engine's signature-image preprocessor must
/// pass JPEG/BMP through untouched, but re-encode every PNG via SkiaSharp so the
/// resulting bytes carry a clean RGBA alpha channel PDFsharp's reader can render
/// correctly. Regression coverage for the v2.0 "black rectangle behind the
/// signature" bug.
/// </summary>
public sealed class SignatureImageNormalizerTests
{
    /// <summary>
    /// JPEG inputs must pass through unchanged — JPEG has no alpha to normalise,
    /// and any re-encode would just lose quality.
    /// </summary>
    [Fact]
    public void Normalize_JpegInput_ReturnsBytesUnchanged()
    {
        var jpeg = MakeJpegBytes(width: 16, height: 16);
        var result = SignatureImageNormalizer.Normalize(jpeg);
        Assert.Equal(jpeg, result);
    }

    /// <summary>
    /// PNG inputs come back as PNG (well-formed magic header + IHDR), even if
    /// the input was already a clean PNG. The point isn't to match bytes — Skia
    /// reorganises chunks — but to land in colour-type 6 (truecolour with alpha)
    /// so the alpha channel is preserved end-to-end.
    /// </summary>
    [Fact]
    public void Normalize_PngInput_ReturnsValidPngWithAlphaChannel()
    {
        var png = MakeTransparentPngWithStroke(width: 32, height: 32);
        var result = SignatureImageNormalizer.Normalize(png);

        // Magic header check (8 bytes, ISO 15948).
        var expectedMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(result.Length > expectedMagic.Length);
        for (var i = 0; i < expectedMagic.Length; i++)
        {
            Assert.Equal(expectedMagic[i], result[i]);
        }

        // Round-trip via Skia to confirm the alpha channel survives.
        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.Equal(SKAlphaType.Premul, decoded.AlphaType);

        // The synthetic input has ONE opaque dark pixel at (16, 16) and 32x32-1
        // fully transparent pixels. After normalise the opaque pixel must still
        // be opaque (alpha == 255) — that's the v2.0 regression we're guarding.
        var inkPixel = decoded.GetPixel(16, 16);
        Assert.Equal((byte)255, inkPixel.Alpha);

        // And a "background" pixel — picking (0, 0) — must STILL be transparent
        // (alpha == 0), proving Skia didn't flatten alpha to opaque white.
        var bgPixel = decoded.GetPixel(0, 0);
        Assert.Equal((byte)0, bgPixel.Alpha);
    }

    /// <summary>
    /// Inputs shorter than the 8-byte PNG magic should still pass through cleanly
    /// without throwing. This is a sanity check that the magic-bytes sniff is
    /// length-safe.
    /// </summary>
    [Fact]
    public void Normalize_ShortInput_PassesThroughWithoutException()
    {
        var stub = new byte[] { 0x89, 0x50 }; // only the first two PNG bytes
        var result = SignatureImageNormalizer.Normalize(stub);
        Assert.Equal(stub, result);
    }

    /// <summary>
    /// Empty input is degenerate but must not crash — the caller (the engine
    /// field-stamper) may receive a zero-length value for an empty signature
    /// field upstream.
    /// </summary>
    [Fact]
    public void Normalize_EmptyInput_ReturnsEmpty()
    {
        var result = SignatureImageNormalizer.Normalize(ReadOnlySpan<byte>.Empty);
        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // Test helpers — build synthetic image bytes via SkiaSharp so we don't
    // depend on on-disk fixtures.
    // -----------------------------------------------------------------------

    private static byte[] MakeTransparentPngWithStroke(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            // Leave the canvas transparent everywhere by default.
            canvas.Clear(SKColors.Transparent);

            // Drop a single fully-opaque dark pixel in the centre — this is
            // the "ink" that has to survive normalisation.
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(new SKRect(width / 2, height / 2, width / 2 + 1, height / 2 + 1), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] MakeJpegBytes(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
