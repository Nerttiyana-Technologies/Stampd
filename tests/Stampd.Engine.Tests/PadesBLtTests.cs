using System.Security.Cryptography.X509Certificates;
using System.Text;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Revocation;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Rendering;
using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Regression tests pinning the PAdES B-LT enrichment path. Uses a stub revocation provider
/// that returns canned bytes — we're asserting that the engine wires up the DSS dictionary
/// correctly, not that real OCSP responders are reachable from CI.
/// </summary>
public sealed class PadesBLtTests
{
    [Fact]
    public async Task Engine_WithBltLevel_EmbedsDssDictionary()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var certificate = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-LT Test Signer",
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
        var pdfText = Encoding.Latin1.GetString(signed);

        // /DSS must appear in the document (in the catalog).
        Assert.Contains("/DSS", pdfText, StringComparison.Ordinal);

        // Both /OCSPs and /CRLs arrays should be present because the stub returned both.
        // We don't crack the PDF object structure here — a substring match is enough to
        // prove the DSS writer ran and emitted the right keys. Detailed structural assertions
        // belong in an integration test that opens the file with PdfSharp again.
        Assert.Contains("/OCSPs", pdfText, StringComparison.Ordinal);
        Assert.Contains("/CRLs", pdfText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_WithBbLevel_OmitsDss()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var certificate = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-B Baseline Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(certificate);
        var revocationProvider = new StubRevocationProvider(
            ocsp: "should-not-be-used"u8.ToArray(),
            crl: null);

        // Even though a revocation provider is wired, target-level BB should bypass it.
        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: null,
            revocationProvider: revocationProvider);

        var request = BuildRequest(sourcePdf, PAdESLevel.BB);
        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        var pdfText = Encoding.Latin1.GetString(signed);
        Assert.DoesNotContain("/DSS", pdfText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_WithBltLevel_NoRevocationProvider_StillSucceedsAtBT()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var certificate = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-LT Fallback Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(certificate);

        // Asking for B-LT but wiring no IRevocationProvider must not blow up — the engine
        // gracefully degrades.
        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: null,
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLT);
        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        Assert.NotNull(signed);
        Assert.True(signed.Length > 0);

        var pdfText = Encoding.Latin1.GetString(signed);
        Assert.DoesNotContain("/DSS", pdfText, StringComparison.Ordinal);
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
            [0] = "Signed by Stampd B-LT Test"u8.ToArray(),
        },
        Sealing = new SealingOptions { TargetLevel = level },
        Metadata = new SignatureMetadata(Reason: "regression test"),
    };

    /// <summary>
    /// Test-only revocation provider that returns canned bytes for every cert. Lets us
    /// assert DSS embedding without depending on network reachability of real OCSP / CRL
    /// endpoints.
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
