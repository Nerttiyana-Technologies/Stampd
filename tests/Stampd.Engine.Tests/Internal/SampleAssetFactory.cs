using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Stampd.Engine.Tests.Internal;

/// <summary>
/// Generates synthetic PDFs and signature images so tests are fully self-contained — no
/// fixture files checked in. Mirrors the smoke test's asset factory.
/// </summary>
internal static class SampleAssetFactory
{
    /// <summary>Builds a one-page Letter PDF with a labelled signature box.</summary>
    public static byte[] BuildSourcePdf()
    {
        using var document = new PdfDocument();
        document.Info.Title = "Stampd Engine Test — Sample";

        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.Letter;

        using var gfx = XGraphics.FromPdfPage(page);
        var titleFont = new XFont("Helvetica", 18, XFontStyleEx.Bold);
        var bodyFont = new XFont("Helvetica", 11, XFontStyleEx.Regular);

        gfx.DrawString(
            "Stampd Engine — Test Contract",
            titleFont,
            XBrushes.Black,
            new XRect(0, 50, page.Width.Point, 30),
            XStringFormats.TopCenter);

        gfx.DrawString(
            "Synthetic test document for the engine regression test suite.",
            bodyFont,
            XBrushes.Black,
            new XRect(72, 120, page.Width.Point - 144, 200),
            XStringFormats.TopLeft);

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a tiny 1x1 transparent PNG. Tests don't need realistic signatures — the
    /// engine accepts any byte sequence XImage.FromStream can decode.
    /// </summary>
    public static byte[] BuildPlaceholderPng()
    {
        // Hand-rolled minimal 1x1 RGBA PNG (transparent pixel). 67 bytes.
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];
    }
}
