using System.Security.Cryptography;

using Stampd.Core;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Rendering;
using Stampd.Engine.Tests.Internal;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Regression tests pinning the wins from the engine spike + provider extraction sessions.
/// Each test guards against a specific bug we hit and debugged at length. The fixture
/// signs a synthetic PDF once and shares it across asserts — signing is non-trivial work
/// and we'd rather not pay for it per test.
/// </summary>
public sealed class EngineSignatureTests : IClassFixture<EngineSignatureTests.SignedPdfFixture>
{
    private readonly SignedPdfFixture _fixture;

    public EngineSignatureTests(SignedPdfFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Guards the killer bug: PdfSharp's RangedStream slow path was returning zero bytes,
    /// causing BouncyCastle to sign the SHA-256 of an empty string. The dispositive
    /// fingerprint of that bug is messageDigest == SHA-256("").
    /// </summary>
    [Fact]
    public void MessageDigest_IsNeverTheEmptyStringHash()
    {
        var emptyHash = SHA256.HashData(ReadOnlySpan<byte>.Empty);
        Assert.NotEqual(emptyHash, _fixture.Inspector.MessageDigest);
    }

    /// <summary>
    /// Adobe verifies signatures by hashing the byte range bytes and comparing to
    /// messageDigest in signedAttrs. These must match exactly, or Adobe shows
    /// "document has been altered or corrupted since the Signature was applied".
    /// </summary>
    [Fact]
    public void MessageDigest_MatchesSha256OfByteRangeBytes()
    {
        var byteRangeBytes = _fixture.Inspector.ByteRangeBytes();
        var expected = SHA256.HashData(byteRangeBytes);
        Assert.Equal(expected, _fixture.Inspector.MessageDigest);
    }

    /// <summary>
    /// /ByteRange must bracket the /Contents hex value exactly. Anything else means the
    /// signed bytes don't correspond to the file regions Adobe will hash.
    /// </summary>
    [Fact]
    public void ByteRange_ExcludesExactlyTheContentsHexValue()
    {
        var inspector = _fixture.Inspector;

        Assert.Equal(0, inspector.ByteRange[0]);
        Assert.Equal(inspector.ContentsOpenIndex, inspector.ByteRange[1]);
        Assert.Equal(inspector.ContentsCloseIndex + 1, inspector.ByteRange[2]);
        Assert.Equal(
            inspector.File.Length - (inspector.ContentsCloseIndex + 1),
            inspector.ByteRange[3]);
    }

    /// <summary>
    /// PAdES (ETSI EN 319 142) mandates DER encoding for the CMS. BouncyCastle defaults
    /// to BER with indefinite-length forms; if we ever regress on the explicit
    /// GetEncoded(Asn1Encodable.Der) call, the first byte's length-of-length will signal
    /// it. DER definite-length SEQUENCE: 0x30 followed by a non-0x80 length byte. BER
    /// indefinite-length: 0x30 followed by 0x80.
    /// </summary>
    [Fact]
    public void Cms_IsDerEncoded_NotBerIndefiniteLength()
    {
        var cms = _fixture.Inspector.CmsBytes;

        Assert.Equal(0x30, cms[0]); // SEQUENCE
        Assert.NotEqual(0x80, cms[1]); // 0x80 == indefinite-length BER form
    }

    /// <summary>
    /// RSA-2048 produces 256-byte signatures. If this drifts (e.g. someone swaps to
    /// RSA-3072 or ECDSA) we want a deliberate test update, not a silent change.
    /// </summary>
    [Fact]
    public void Signature_Is256BytesForRsa2048()
    {
        var sigBytes = _fixture.Inspector.FirstSigner.GetSignature();
        Assert.Equal(256, sigBytes.Length);
    }

    /// <summary>
    /// Without an ITimestampAuthorityProvider configured, the engine must produce
    /// PAdES B-B (no embedded timestamp). The fixture wires no TSA.
    /// </summary>
    [Fact]
    public void WithoutTsa_ProducesPadesBB_NoEmbeddedTimestamp()
    {
        Assert.False(_fixture.Inspector.HasEmbeddedTimestamp);
    }

    /// <summary>
    /// The signed PDF must start with the standard PDF header. Catches the case where
    /// we accidentally write something other than a PDF (e.g. raw CMS).
    /// </summary>
    [Fact]
    public void Output_StartsWithPdfHeader()
    {
        var header = System.Text.Encoding.ASCII.GetString(_fixture.SignedPdf, 0, 5);
        Assert.Equal("%PDF-", header);
    }

    /// <summary>
    /// Shared signing fixture: build a synthetic PDF, sign it once with a self-signed
    /// cert, parse the result, and expose the inspector to every test in the class.
    /// </summary>
    public sealed class SignedPdfFixture
    {
        public byte[] SignedPdf { get; }
        public SignedPdfInspector Inspector { get; }

        public SignedPdfFixture()
        {
            // PdfSharp 6.x has no default font resolver outside Windows.
            PlatformFontResolver.Register();

            var sourcePdf = SampleAssetFactory.BuildSourcePdf();

            using var certificate = SelfSignedCertificateFactory.Create(
                subjectCommonName: "Stampd Test Signer",
                validity: TimeSpan.FromDays(30));

            var sealingProvider = new LocalCertificateSealingProvider(certificate);
            var engine = new PdfSharpStampdEngine(sealingProvider);

            // Use a text field rather than an image field — the tests need to exercise
            // the signing pipeline, not PdfSharp's image decoder. Text fields take any
            // UTF-8 string and avoid the dependency on a valid PNG decoder.
            var request = new SignatureRequest
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
                    [0] = System.Text.Encoding.UTF8.GetBytes("Signed by Stampd Test"),
                },
                Sealing = new SealingOptions(),
                Metadata = new SignatureMetadata(
                    Reason: "regression test",
                    Location: "Stampd.Engine.Tests",
                    SignerName: "Test"),
            };

            SignedPdf = engine.SignAsync(request).GetAwaiter().GetResult().SignedPdf.ToArray();
            Inspector = SignedPdfInspector.Parse(SignedPdf);
        }
    }
}
