using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using PdfSharp.Pdf.IO;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Revocation;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Rendering;
using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Regression tests for the strict-ETSI B-LT incremental update path. Asserts that:
///
/// 1. The DSS dictionary lives in the bytes APPENDED after the original signed region —
///    not inside the signature's /ByteRange. This is the strict-ETSI shape that distinguishes
///    v1.2's incremental-update profile from v1.1's pre-embed profile.
/// 2. The signature's /ByteRange still validates against its messageDigest after the
///    incremental update. If the writer accidentally modified any signed byte, the digest
///    would not match.
/// 3. PdfSharp can re-open the resulting PDF without parse errors — proves the new xref,
///    trailer, and object stream are structurally well-formed.
/// </summary>
public sealed class PadesIncrementalUpdateTests
{
    [Fact]
    public async Task Engine_WithBltLevel_PlacesDssInAppendedRevision()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var certificate = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-LT Incremental Update Test Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(certificate);
        var revocationProvider = new StubRevocationProvider(
            ocsp: "fake-ocsp-bytes"u8.ToArray(),
            crl: "fake-crl-bytes"u8.ToArray());

        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: null,
            revocationProvider: revocationProvider);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLT);
        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        // ---- 1. DSS is after the signed region ----
        var inspector = SignedPdfInspector.Parse(signed);
        var signedRegionEnd = inspector.ByteRange[2] + inspector.ByteRange[3];

        var latin1 = Encoding.Latin1.GetString(signed);
        var dssMarkerIdx = latin1.IndexOf("/DSS", StringComparison.Ordinal);
        Assert.True(dssMarkerIdx >= 0, "Signed PDF must contain a /DSS marker.");
        Assert.True(
            dssMarkerIdx > signedRegionEnd,
            $"/DSS marker at byte offset {dssMarkerIdx} must be AFTER the signed region end ({signedRegionEnd}). "
            + "If it's inside, the writer reverted to the v1.1 pre-embed shape.");

        // The /Certs, /OCSPs, /CRLs subkeys should also be in the appended region.
        var certsIdx = latin1.IndexOf("/Certs", StringComparison.Ordinal);
        var ocspsIdx = latin1.IndexOf("/OCSPs", StringComparison.Ordinal);
        var crlsIdx = latin1.IndexOf("/CRLs", StringComparison.Ordinal);
        Assert.True(certsIdx > signedRegionEnd, "/Certs must live in the appended revision.");
        Assert.True(ocspsIdx > signedRegionEnd, "/OCSPs must live in the appended revision.");
        Assert.True(crlsIdx > signedRegionEnd, "/CRLs must live in the appended revision.");

        // ---- 2. /ByteRange still hashes correctly ----
        var byteRangeBytes = inspector.ByteRangeBytes();
        var computedDigest = SHA256.HashData(byteRangeBytes);
        Assert.Equal(computedDigest, inspector.MessageDigest);

        // ---- 3. PdfSharp can re-open the result ----
        // Tests that the appended xref + trailer + objects are structurally well-formed:
        // any offset miscount, missing 'endobj', or malformed dict would throw here.
        using var ms = new MemoryStream(signed, writable: false);
        using var reopened = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        Assert.True(reopened.PageCount > 0);

        // The reopened document's catalog should expose /DSS, confirming the catalog
        // override in the appended xref correctly took precedence over the original.
        var dssEntry = reopened.Internals.Catalog.Elements["/DSS"];
        Assert.NotNull(dssEntry);
    }

    [Fact]
    public async Task Engine_WithBltLevel_PreservesSignatureValidityAcrossIncrementalUpdate()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var certificate = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-LT Signature Preservation Test Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(certificate);
        var revocationProvider = new StubRevocationProvider(
            ocsp: "fake-ocsp-bytes"u8.ToArray(),
            crl: null);

        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: null,
            revocationProvider: revocationProvider);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLT);
        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        // The signature's signed-attributes messageDigest must hash to SHA-256 over the
        // /ByteRange bytes. Even after the incremental update is appended, this must hold —
        // any single-byte modification inside the signed region would break it.
        var inspector = SignedPdfInspector.Parse(signed);
        var byteRangeBytes = inspector.ByteRangeBytes();
        var computedDigest = SHA256.HashData(byteRangeBytes);
        Assert.Equal(computedDigest, inspector.MessageDigest);
    }

    private static SignatureRequest BuildRequest(byte[] sourcePdf, PAdESLevel level) => new()
    {
        SourcePdf = sourcePdf,
        Fields =
        [
            new SignatureField(
                PageNumber: 1,
                Bounds: new PercentageRect(X: 10, Y: 80, Width: 30, Height: 4),
                Kind: SignatureFieldKind.Text,
                SignerId: "test-signer"),
        ],
        FieldValues = new Dictionary<int, ReadOnlyMemory<byte>>
        {
            [0] = "Signed by Stampd B-LT incremental update test"u8.ToArray(),
        },
        Sealing = new SealingOptions { TargetLevel = level },
        Metadata = new SignatureMetadata(Reason: "regression test"),
    };

    /// <summary>
    /// Test-only revocation provider that returns canned bytes for every cert. Same shape
    /// as the one in PadesBLtTests — kept local to avoid cross-fixture coupling.
    /// </summary>
    private sealed class StubRevocationProvider : IRevocationProvider
    {
        private readonly byte[]? _ocsp;
        private readonly byte[]? _crl;

        public StubRevocationProvider(byte[]? ocsp, byte[]? crl)
        {
            _ocsp = ocsp;
            _crl = crl;
        }

        public string Name => "Stub";

        public Task<CertificateRevocationInfo> GetRevocationInfoAsync(
            X509Certificate2 certificate,
            X509Certificate2 issuer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CertificateRevocationInfo(_ocsp, _crl));
    }
}
