using System.Security.Cryptography;
using System.Text;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Sealing;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Rendering;
using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// v1.3 #132 — regression tests for the PAdES Document Timestamp (Part 4 B-LTA carrier).
/// PadesBLtaTests covers the CMS-level archive-time-stamp-v3 attribute (Part 2 carrier);
/// this fixture covers the parallel /Type /DocTimeStamp signature dict appended as a
/// separate incremental revision past the DSS.
/// </summary>
public sealed class PadesDocTimeStampTests
{
    [Fact]
    public async Task Engine_WithBltaLevel_AppendsDocTimeStampRevision()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd DocTimeStamp Test Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(signerCert);
        var stubTsa = new PadesBLtaTests.InProcessStubTsaProvider();

        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: stubTsa,
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLTA);

        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        Assert.NotNull(signed);

        // Three TSA round-trips for B-LTA in v1.3:
        //   1. Signature TST (embedded in CMS as id-aa-signatureTimeStampToken)
        //   2. CMS-level archive-time-stamp-v3 (Part 2 long-term anchor, with strict
        //      ATSHashIndexV3 imprint per v1.3 #131)
        //   3. PAdES /DocTimeStamp signature dict (Part 4 long-term anchor, this test)
        Assert.Equal(3, stubTsa.CallCount);
    }

    [Fact]
    public async Task Engine_WithBltaLevel_DocTimeStampHasCorrectSubFilterAndType()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd DocTimeStamp Wire Test Signer",
            validity: TimeSpan.FromDays(30));

        var engine = new PdfSharpStampdEngine(
            new LocalCertificateSealingProvider(signerCert),
            timestampAuthority: new PadesBLtaTests.InProcessStubTsaProvider(),
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLTA);

        var signed = (await engine.SignAsync(request, TestContext.Current.CancellationToken))
            .SignedPdf.ToArray();

        // The Document Timestamp lives in its own appended revision. Search for the
        // /Type /DocTimeStamp marker — case-sensitive, must exist somewhere past the
        // original signature.
        var bytes = signed;
        var docTimeStampMarkerIdx = IndexOf(bytes, "/Type /DocTimeStamp"u8.ToArray());
        Assert.True(docTimeStampMarkerIdx > 0,
            "Signed PDF must contain a /Type /DocTimeStamp marker for v1.3 B-LTA.");

        // /SubFilter must be /ETSI.RFC3161 — the PAdES Part 4 mandate for document
        // timestamps. Any other SubFilter (notably /ETSI.CAdES.detached used for normal
        // sigs) means the dict is misclassified.
        var subFilterMarkerIdx = IndexOf(bytes, "/SubFilter /ETSI.RFC3161"u8.ToArray());
        Assert.True(subFilterMarkerIdx > docTimeStampMarkerIdx,
            "Signed PDF must declare /SubFilter /ETSI.RFC3161 for the document timestamp.");
    }

    [Fact]
    public async Task Engine_WithBltaLevel_DocTimeStampComesAfterOriginalSignature()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd DocTimeStamp Order Test Signer",
            validity: TimeSpan.FromDays(30));

        var engine = new PdfSharpStampdEngine(
            new LocalCertificateSealingProvider(signerCert),
            timestampAuthority: new PadesBLtaTests.InProcessStubTsaProvider(),
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLTA);

        var signed = (await engine.SignAsync(request, TestContext.Current.CancellationToken))
            .SignedPdf.ToArray();

        // The DocTimeStamp must live in an APPENDED revision past the original signed
        // region — that's the strict-ETSI incremental-update shape. We verify this
        // structurally by checking the DocTimeStamp marker appears past the second-
        // to-last %%EOF: every incremental update closes with its own %%EOF, so the
        // original sig's region ends at the first %%EOF and the DocTimeStamp lives
        // strictly between that and the final %%EOF.
        //
        // This is more robust than comparing against a /SubFilter string of the
        // original signature — PdfSharp 6.x may emit /SubFilter with or without
        // whitespace between key and value, and the original signature's filter is
        // a PdfSharp implementation detail rather than something we control.
        var docTimeStampIdx = IndexOf(signed, "/Type /DocTimeStamp"u8.ToArray());
        Assert.True(docTimeStampIdx > 0, "/Type /DocTimeStamp marker must exist.");

        var eofOffsets = AllOccurrences(signed, "%%EOF"u8.ToArray());
        Assert.True(eofOffsets.Count >= 2,
            $"Expected at least two %%EOF markers (one per revision); found {eofOffsets.Count}.");

        var firstEofOffset = eofOffsets[0];
        Assert.True(docTimeStampIdx > firstEofOffset,
            "Document Timestamp must come AFTER the original signature revision's %%EOF " +
            "(strict-ETSI incremental-update shape).");
    }

    [Fact]
    public async Task Engine_WithBltaLevel_OutputReopensWithPdfSharp()
    {
        // If the catalog patch, /AcroForm revision, or xref table is malformed, PdfSharp
        // will fail to re-parse the file. This is the cheapest end-to-end "did we corrupt
        // the PDF structure" test.
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd DocTimeStamp Reopen Test Signer",
            validity: TimeSpan.FromDays(30));

        var engine = new PdfSharpStampdEngine(
            new LocalCertificateSealingProvider(signerCert),
            timestampAuthority: new PadesBLtaTests.InProcessStubTsaProvider(),
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLTA);
        var signed = (await engine.SignAsync(request, TestContext.Current.CancellationToken))
            .SignedPdf.ToArray();

        // PdfSharp 6.x deprecated ReadOnly in favor of Import (the underlying open mode
        // that doesn't permit modifications). Same parse-or-fail semantic for our needs.
        using var ms = new MemoryStream(signed);
        var reopened = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        Assert.True(reopened.PageCount > 0);
    }

    [Fact]
    public async Task Engine_WithBltLevel_OmitsDocTimeStamp()
    {
        // Sanity check: only BLTA should append the document timestamp. BLT must NOT
        // touch the post-DSS region, otherwise we'd silently over-collateralize signatures
        // adopters asked to keep at PAdES baseline B-LT.
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd BLT (No DocTimeStamp) Test Signer",
            validity: TimeSpan.FromDays(30));

        var engine = new PdfSharpStampdEngine(
            new LocalCertificateSealingProvider(signerCert),
            timestampAuthority: new PadesBLtaTests.InProcessStubTsaProvider(),
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLT);

        var signed = (await engine.SignAsync(request, TestContext.Current.CancellationToken))
            .SignedPdf.ToArray();

        var docTimeStampMarkerIdx = IndexOf(signed, "/Type /DocTimeStamp"u8.ToArray());
        Assert.True(docTimeStampMarkerIdx < 0,
            "BLT-level signatures must NOT contain a /DocTimeStamp dict.");
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
            [0] = "Signed by Stampd DocTimeStamp Test"u8.ToArray(),
        },
        Sealing = new SealingOptions { TargetLevel = level },
        Metadata = new SignatureMetadata(Reason: "regression test"),
    };

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static List<int> AllOccurrences(byte[] haystack, byte[] needle)
    {
        var hits = new List<int>();
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) hits.Add(i);
        }
        return hits;
    }
}
