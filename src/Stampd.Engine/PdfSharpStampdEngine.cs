using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Revocation;
using Stampd.Core.Sealing;
using Stampd.Engine.Pades;
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
    private readonly IRevocationProvider? _revocationProvider;

    public PdfSharpStampdEngine(
        ICryptographicSealingProvider sealingProvider,
        ITimestampAuthorityProvider? timestampAuthority = null,
        IRevocationProvider? revocationProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sealingProvider);
        _sealingProvider = sealingProvider;
        _timestampAuthority = timestampAuthority;
        _revocationProvider = revocationProvider;
    }

    /// <inheritdoc />
    public async Task<SignedDocument> SignAsync(
        SignatureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // B-LT requires pre-fetching revocation info for the cert chain BEFORE we open the
        // PDF for signing. Done here (async) instead of inside SignCore (sync).
        PadesRevocationData? revocationData = null;
        if (ShouldEmbedRevocationInfo(request.Sealing.TargetLevel) && _revocationProvider is not null)
        {
            var signingCert = await _sealingProvider
                .GetSigningCertificateAsync(cancellationToken)
                .ConfigureAwait(false);
            var fetcher = new PadesRevocationFetcher(_revocationProvider);
            revocationData = await fetcher.GatherAsync(signingCert, cancellationToken).ConfigureAwait(false);
        }

        return await SignCoreAsync(request, revocationData, cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldEmbedRevocationInfo(PAdESLevel target)
        => target == PAdESLevel.BLT || target == PAdESLevel.BLTA;

    private async Task<SignedDocument> SignCoreAsync(
        SignatureRequest request,
        PadesRevocationData? revocationData,
        CancellationToken cancellationToken)
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
        var effectiveTsa = request.Sealing.TargetLevel == PAdESLevel.BB
            ? null
            : _timestampAuthority;
        var cmsBuilder = new PadesCmsBuilder(
            _sealingProvider,
            hashAlgorithm,
            effectiveTsa,
            targetLevel: request.Sealing.TargetLevel);

        // B-LTA stacks an archive timestamp on top of the B-T timestamp; both TSTs carry
        // the TSA cert chain, so /Contents needs roughly twice the room the B-T path used.
        var signatureSize = request.Sealing.TargetLevel == PAdESLevel.BLTA
            ? ProviderBackedDigitalSigner.ReservedSizeBlta
            : ProviderBackedDigitalSigner.ReservedSizeDefault;

        byte[] signedPdf;
        using (var stampedStream = new MemoryStream(stampedPdf, writable: false))
        using (var signingDocument = PdfReader.Open(stampedStream, PdfDocumentOpenMode.Modify))
        {
            // DSS embedding is now a POST-signing concern handled by
            // PadesIncrementalUpdateWriter — the strict-ETSI shape (DSS in an incremental
            // update revision after the signature). The pre-signing PadesDssWriter is
            // retained in the codebase for adopters who specifically need the v1.1
            // "DSS-inside-byterange" shape, but is no longer wired into the default flow.

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
                new ProviderBackedDigitalSigner(cmsBuilder, certificateName: "Stampd Signer", reservedSize: signatureSize),
                options);

            using var signedBuffer = new MemoryStream();
            signingDocument.Save(signedBuffer, closeStream: false);
            signedPdf = signedBuffer.ToArray();
        }

        // ---- Phase 3: append B-LT DSS as a strict-ETSI incremental update ----
        // Only runs when revocation data was gathered (target level B-LT or B-LTA AND
        // an IRevocationProvider was configured AND it returned something). The writer
        // appends a new PDF revision behind the existing %%EOF that adds /DSS to the
        // catalog without modifying any signed bytes — so the signature's /ByteRange
        // continues to validate against the original content.
        if (revocationData is not null && !revocationData.IsEmpty)
        {
            signedPdf = PadesIncrementalUpdateWriter.AppendDss(signedPdf, revocationData);
        }

        // ---- Phase 4: append PAdES Document Timestamp for B-LTA (v1.3 #132) ----
        // ETSI EN 319 142-1 §5.4 defines B-LTA conformance via Document Timestamps —
        // a separate /Type /DocTimeStamp signature dict appended as another incremental
        // update revision past the DSS. This complements the v1.2 CMS-level archive-TST
        // (whose imprint is now strict per #131); together they cover both PAdES Part 2
        // and Part 4 verifiers. Skipped silently if no TSA is configured — adopters can
        // still target B-T or B-LT without a TSA.
        if (request.Sealing.TargetLevel == PAdESLevel.BLTA && effectiveTsa is not null)
        {
            signedPdf = await PadesDocTimeStampWriter
                .AppendAsync(signedPdf, effectiveTsa, hashAlgorithm, cancellationToken)
                .ConfigureAwait(false);
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
        internal const int ReservedSizeDefault = 32 * 1024;

        // B-LTA carries a second TST (the archive timestamp) embedded as a separate
        // unsigned attribute, so /Contents needs roughly twice the room.
        internal const int ReservedSizeBlta = 64 * 1024;

        private readonly PadesCmsBuilder _cmsBuilder;
        private readonly int _reservedSize;

        public ProviderBackedDigitalSigner(PadesCmsBuilder cmsBuilder, string certificateName, int reservedSize = ReservedSizeDefault)
        {
            _cmsBuilder = cmsBuilder;
            CertificateName = certificateName;
            _reservedSize = reservedSize;
        }

        public string CertificateName { get; }

        public Task<int> GetSignatureSizeAsync() => Task.FromResult(_reservedSize);

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
