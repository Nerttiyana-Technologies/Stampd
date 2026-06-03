using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

using Stampd.Core;
using Stampd.Core.Sealing;
using Stampd.Engine.Sealing;

namespace Stampd.Engine;

/// <summary>
/// PDFsharp + BouncyCastle implementation of <see cref="IStampdEngine"/>. Delegates the
/// cryptographic primitive to an <see cref="ICryptographicSealingProvider"/>, so the
/// engine itself is agnostic to local-vs-Vault-vs-Azure-Key-Vault signing.
/// </summary>
/// <remarks>
/// Flow per <see cref="SignAsync"/>:
/// 1. Open source PDF (PdfSharp).
/// 2. For each field that has a value, draw the value onto its page at the resolved
///    absolute coordinates (XGraphics).
/// 3. Save the stamped PDF to a memory stream.
/// 4. Re-open with <see cref="PdfDocumentOpenMode.Modify"/>, configure a digital signature,
///    and have PDFsharp's signing pipeline call back into <see cref="PadesCmsBuilder"/>
///    over the /ByteRange.
/// 5. Compute SHA-256 over the final bytes, return.
///
/// Spike scope: PAdES B-B. Trusted-timestamp embedding (B-T) is a follow-up that lives
/// inside <see cref="PadesCmsBuilder"/>.
/// </remarks>
public sealed class PdfSharpStampdEngine : IStampdEngine
{
    private readonly ICryptographicSealingProvider _sealingProvider;
    private readonly ITimestampAuthorityProvider? _timestampAuthority;

    public PdfSharpStampdEngine(
        ICryptographicSealingProvider sealingProvider,
        ITimestampAuthorityProvider? timestampAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(sealingProvider);
        _sealingProvider = sealingProvider;
        _timestampAuthority = timestampAuthority;
    }

    /// <inheritdoc />
    public Task<SignedDocument> SignAsync(
        SignatureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var signed = SignCore(request, cancellationToken);
        return Task.FromResult(signed);
    }

    private SignedDocument SignCore(SignatureRequest request, CancellationToken cancellationToken)
    {
        // ---- Phase 1: stamp the visible content onto the PDF ----
        byte[] stampedPdf;
        using (var sourceStream = new MemoryStream(request.SourcePdf.ToArray()))
        using (var document = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Modify))
        {
            for (var fieldIndex = 0; fieldIndex < request.Fields.Count; fieldIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var field = request.Fields[fieldIndex];
                if (!request.FieldValues.TryGetValue(fieldIndex, out var value))
                {
                    continue;
                }

                if (field.PageNumber < 1 || field.PageNumber > document.PageCount)
                {
                    throw new InvalidOperationException(
                        $"Field {fieldIndex} references page {field.PageNumber} but the document has {document.PageCount} pages.");
                }

                field.Bounds.EnsureValid();
                var page = document.Pages[field.PageNumber - 1];

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                StampField(gfx, page, field, value.Span);
            }

            using var stampedBuffer = new MemoryStream();
            document.Save(stampedBuffer, closeStream: false);
            stampedPdf = stampedBuffer.ToArray();
        }

        // ---- Phase 2: apply the X.509 signature via the configured provider ----
        var hashAlgorithm = ParseHashAlgorithm(request.Sealing.DigestAlgorithm);
        var cmsBuilder = new PadesCmsBuilder(_sealingProvider, hashAlgorithm, _timestampAuthority);

        byte[] signedPdf;
        using (var stampedStream = new MemoryStream(stampedPdf, writable: false))
        using (var signingDocument = PdfReader.Open(stampedStream, PdfDocumentOpenMode.Modify))
        {
            var options = new DigitalSignatureOptions
            {
                ContactInfo = request.Metadata?.ContactInfo ?? string.Empty,
                Location = request.Metadata?.Location ?? string.Empty,
                Reason = request.Metadata?.Reason ?? "Signed via Stampd",
                AppearanceHandler = null,
            };

            // CertificateName is the /Name entry in the PDF signature dictionary. Keep it
            // short and constant; PDFsharp 6.2's offset accounting in the signing pipeline
            // computes /ByteRange before our signer is called, and a longer-than-expected
            // /Name (e.g. taking it from request.Metadata?.SignerName) has been observed
            // to push /Contents past the position recorded in /ByteRange, which yields the
            // "document altered since signed" verdict in Adobe Acrobat.
            _ = DigitalSignatureHandler.ForDocument(
                signingDocument,
                new ProviderBackedDigitalSigner(cmsBuilder, certificateName: "Stampd Signer"),
                options);

            using var signedBuffer = new MemoryStream();
            signingDocument.Save(signedBuffer, closeStream: false);
            signedPdf = signedBuffer.ToArray();
        }

        var hashHex = Convert.ToHexString(SHA256.HashData(signedPdf)).ToLowerInvariant();
        return new SignedDocument(signedPdf, hashHex, DateTimeOffset.UtcNow);
    }

    private static HashAlgorithmName ParseHashAlgorithm(string name) => name.ToUpperInvariant() switch
    {
        "SHA-256" or "SHA256" => HashAlgorithmName.SHA256,
        "SHA-384" or "SHA384" => HashAlgorithmName.SHA384,
        "SHA-512" or "SHA512" => HashAlgorithmName.SHA512,
        _ => throw new NotSupportedException($"Unsupported digest algorithm: '{name}'."),
    };

    private static void StampField(
        XGraphics gfx,
        PdfPage page,
        SignatureField field,
        ReadOnlySpan<byte> value)
    {
        var pageW = page.Width.Point;
        var pageH = page.Height.Point;

        var absX = field.Bounds.X / 100.0 * pageW;
        var absY = field.Bounds.Y / 100.0 * pageH;
        var absW = field.Bounds.Width / 100.0 * pageW;
        var absH = field.Bounds.Height / 100.0 * pageH;
        var rect = new XRect(absX, absY, absW, absH);

        switch (field.Kind)
        {
            case SignatureFieldKind.Signature:
            case SignatureFieldKind.Initials:
                {
                    var bytes = value.ToArray();
                    using var imageStream = new MemoryStream(bytes, writable: false);
                    using var image = XImage.FromStream(imageStream);
                    gfx.DrawImage(image, rect);
                    break;
                }

            case SignatureFieldKind.Date:
            case SignatureFieldKind.Text:
                {
                    var text = Encoding.UTF8.GetString(value);
                    var font = new XFont("Helvetica", 11, XFontStyleEx.Regular);
                    gfx.DrawString(
                        text,
                        font,
                        XBrushes.Black,
                        rect,
                        XStringFormats.CenterLeft);
                    break;
                }

            case SignatureFieldKind.Checkbox:
                {
                    var checkedState = value.Length > 0 && value[0] != 0;
                    if (checkedState)
                    {
                        var checkFont = new XFont("Helvetica", 14, XFontStyleEx.Bold);
                        gfx.DrawString("X", checkFont, XBrushes.Black, rect, XStringFormats.Center);
                    }

                    var pen = new XPen(XColors.Black, 0.5);
                    gfx.DrawRectangle(pen, rect);
                    break;
                }

            default:
                throw new NotSupportedException(
                    $"Unsupported field kind: {field.Kind.ToString()} ({((int)field.Kind).ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    /// <summary>
    /// Adapts PDFsharp's <c>IDigitalSigner</c> contract to a <see cref="PadesCmsBuilder"/>.
    /// PDFsharp drives signing by reserving space in <c>/Contents</c> and then asking for
    /// the CMS blob over the document's byte range.
    /// </summary>
    private sealed class ProviderBackedDigitalSigner : IDigitalSigner
    {
        // 32 KB comfortably accommodates an RSA-2048 CMS plus an embedded RFC 3161
        // timestamp token (which carries the TSA's own cert chain). Over-reserving is
        // harmless — PDFsharp pads /Contents with zero bytes that Adobe ignores.
        private const int ReservedSignatureSize = 32 * 1024;

        private readonly PadesCmsBuilder _cmsBuilder;

        public ProviderBackedDigitalSigner(PadesCmsBuilder cmsBuilder, string certificateName)
        {
            _cmsBuilder = cmsBuilder;
            CertificateName = certificateName;
        }

        public string CertificateName { get; }

        public Task<int> GetSignatureSizeAsync() => Task.FromResult(ReservedSignatureSize);

        public Task<byte[]> GetSignatureAsync(Stream rangeStream)
        {
            ArgumentNullException.ThrowIfNull(rangeStream);

            // PDFsharp 6.2.x's RangedStream has two Read code paths: a "fast path" that
            // works correctly when offset == 0 && count == Length (it positions the
            // underlying stream into each range explicitly), and a "slow path" that
            // assumes Stream.Position is already inside the first range. By the time
            // PDFsharp hands us the RangedStream, it has already written the entire PDF
            // body, so the underlying stream is positioned at end-of-file — the slow
            // path immediately sees Position == Length and bails out with zero bytes,
            // and BouncyCastle then signs an empty hash that Adobe rejects with
            // "document altered since signed".
            //
            // We deliberately hit the fast path: one Read with offset 0 and count equal
            // to the RangedStream's total length. This is also more efficient than
            // chunking.
            var totalLength = checked((int)rangeStream.Length);
            var data = new byte[totalLength];
            var read = rangeStream.Read(data, 0, totalLength);

            if (read != totalLength)
            {
                throw new InvalidOperationException(
                    $"PDFsharp RangedStream short read: expected {totalLength} bytes, got {read}.");
            }

            return _cmsBuilder.BuildAsync(data, CancellationToken.None);
        }
    }
}
