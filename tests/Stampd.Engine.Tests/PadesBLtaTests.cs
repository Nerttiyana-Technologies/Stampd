using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Sealing;
using Stampd.Crypto.LocalCertificate;
using Stampd.Engine.Rendering;
using Stampd.Engine.Tests.Internal;

using Xunit;

using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace Stampd.Engine.Tests;

/// <summary>
/// Regression tests for the PAdES B-LTA enrichment path. B-LTA layers an archive
/// timestamp on top of the B-T signature timestamp; this fixture verifies that:
///
/// 1. Asking for level BLTA triggers TWO TSA round-trips (one for the signature TST,
///    one for the archive TST), while level BLT triggers only one.
/// 2. The id-aa-ets-archiveTimestampV3 OID (1.2.840.113549.1.9.16.2.48) is present
///    in the resulting CMS unsigned attributes.
/// 3. B-LT remains a single-TST signature — no archive attribute leaks into it.
/// 4. (v1.3 #131) The strict id-aa-ats-hash-index-v3 attribute is present alongside
///    the archive TST, with the correct hashIndAlgorithm and per-category entry
///    counts (certs / CRLs / unsigned attrs).
/// </summary>
public sealed class PadesBLtaTests
{
    // RFC 5816 / ETSI TS 101 733 §6.4.3 — archive-time-stamp-v3 attribute OID.
    private const string ArchiveTimestampV3Oid = "1.2.840.113549.1.9.16.2.48";

    // RFC 7026 — id-aa-ats-hash-index-v3 attribute OID. Locks in the membership of
    // certs/CRLs/unsigned attrs that the archive TST witnessed.
    private const string AtsHashIndexV3Oid = "1.2.840.113549.1.9.16.2.51";

    // NIST OID for SHA-256 — the default hashIndAlgorithm in our engine wiring.
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    [Fact]
    public async Task Engine_WithBltaLevel_RequestsTwoTstsAndEmbedsArchiveAttribute()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-LTA Test Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(signerCert);
        var stubTsa = new InProcessStubTsaProvider();

        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: stubTsa,
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLTA);

        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        Assert.NotNull(signed);
        Assert.True(signed.Length > 0);

        // Three TSA round-trips for v1.3 B-LTA:
        //   1. Signature TST (B-T anchor inside the original signer's CMS)
        //   2. CMS-level archive-time-stamp-v3 (Part 2 anchor; strict ATSHashIndexV3
        //      imprint per v1.3 #131)
        //   3. PAdES /DocTimeStamp signature dict (Part 4 anchor per v1.3 #132)
        Assert.Equal(3, stubTsa.CallCount);

        // Both the B-T attribute and the B-LTA archive attribute must be present on the
        // SignerInfo's unsigned-attributes table.
        var inspector = SignedPdfInspector.Parse(signed);
        var oids = CollectUnsignedAttributeOids(inspector.FirstSigner);
        Assert.Contains(PkcsObjectIdentifiers.IdAASignatureTimeStampToken.Id, oids);
        Assert.Contains(ArchiveTimestampV3Oid, oids);

        // v1.3 #131 — strict ATSHashIndexV3 attribute must also be present, locking
        // in (certs, CRLs, existing unsigned attrs) at archive-time. Without this the
        // archive TST imprint reduces to the v1.2 pragmatic compromise.
        Assert.Contains(AtsHashIndexV3Oid, oids);

        // Verify the hash-index attribute's wire shape: SHA-256 algorithm identifier,
        // one cert in certificatesHashIndex (the signer cert), zero CRLs (we keep
        // revocation in the PDF DSS, not in CMS), and one entry in
        // unsignedAttrValuesHashIndex (the signature-TST that exists before the
        // archive TST is added).
        var index = ParseAtsHashIndex(inspector.FirstSigner);
        Assert.Equal(Sha256Oid, index.HashAlgorithmOid);
        Assert.Equal(1, index.CertificatesCount);
        Assert.Equal(0, index.CrlsCount);
        Assert.Equal(1, index.UnsignedAttrValuesCount);
    }

    [Fact]
    public async Task Engine_WithBltLevel_DoesNotEmbedArchiveAttribute()
    {
        PlatformFontResolver.Register();

        var sourcePdf = SampleAssetFactory.BuildSourcePdf();
        using var signerCert = SelfSignedCertificateFactory.Create(
            subjectCommonName: "Stampd B-LT (No Archive) Test Signer",
            validity: TimeSpan.FromDays(30));

        var sealingProvider = new LocalCertificateSealingProvider(signerCert);
        var stubTsa = new InProcessStubTsaProvider();

        var engine = new PdfSharpStampdEngine(
            sealingProvider,
            timestampAuthority: stubTsa,
            revocationProvider: null);

        var request = BuildRequest(sourcePdf, PAdESLevel.BLT);

        var signedDocument = await engine.SignAsync(request, TestContext.Current.CancellationToken);
        var signed = signedDocument.SignedPdf.ToArray();

        // Exactly one TSA round-trip: just the signature TST.
        Assert.Equal(1, stubTsa.CallCount);

        var inspector = SignedPdfInspector.Parse(signed);
        var oids = CollectUnsignedAttributeOids(inspector.FirstSigner);
        Assert.Contains(PkcsObjectIdentifiers.IdAASignatureTimeStampToken.Id, oids);
        Assert.DoesNotContain(ArchiveTimestampV3Oid, oids);
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
            [0] = "Signed by Stampd B-LTA Test"u8.ToArray(),
        },
        Sealing = new SealingOptions { TargetLevel = level },
        Metadata = new SignatureMetadata(Reason: "regression test"),
    };

    private static HashSet<string> CollectUnsignedAttributeOids(SignerInformation signer)
    {
        var oids = new HashSet<string>(StringComparer.Ordinal);
        var unsigned = signer.UnsignedAttributes;
        if (unsigned is null)
        {
            return oids;
        }

        // AttributeTable in BC.NET 2.6+ exposes its entries via Asn1EncodableVector;
        // each element is an Attribute carrying AttrType (OID) and AttrValues.
        var vec = unsigned.ToAsn1EncodableVector();
        for (var i = 0; i < vec.Count; i++)
        {
            if (vec[i] is Org.BouncyCastle.Asn1.Cms.Attribute attr)
            {
                oids.Add(attr.AttrType.Id);
            }
        }
        return oids;
    }

    /// <summary>
    /// Pulls the ats-hash-index-v3 attribute off the signer's unsigned attrs and
    /// parses its ASN.1 SEQUENCE into a small struct the tests can assert against.
    /// Fields per RFC 7026: hashIndAlgorithm, certificatesHashIndex, crlsHashIndex,
    /// unsignedAttrValuesHashIndex.
    /// </summary>
    private static AtsHashIndexSnapshot ParseAtsHashIndex(SignerInformation signer)
    {
        var unsigned = signer.UnsignedAttributes
            ?? throw new InvalidOperationException("Signer has no unsigned attributes.");
        var vec = unsigned.ToAsn1EncodableVector();

        Org.BouncyCastle.Asn1.Cms.Attribute? hashIndexAttr = null;
        for (var i = 0; i < vec.Count; i++)
        {
            if (vec[i] is Org.BouncyCastle.Asn1.Cms.Attribute a && a.AttrType.Id == AtsHashIndexV3Oid)
            {
                hashIndexAttr = a;
                break;
            }
        }
        Assert.NotNull(hashIndexAttr);

        var sequence = (Asn1Sequence)hashIndexAttr.AttrValues[0];
        var algorithm = Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier.GetInstance(sequence[0]);
        var certs = (Asn1Sequence)sequence[1];
        var crls = (Asn1Sequence)sequence[2];
        var unsignedAttrs = (Asn1Sequence)sequence[3];

        return new AtsHashIndexSnapshot(
            HashAlgorithmOid: algorithm.Algorithm.Id,
            CertificatesCount: certs.Count,
            CrlsCount: crls.Count,
            UnsignedAttrValuesCount: unsignedAttrs.Count);
    }

    private sealed record AtsHashIndexSnapshot(
        string HashAlgorithmOid,
        int CertificatesCount,
        int CrlsCount,
        int UnsignedAttrValuesCount);

    /// <summary>
    /// In-process stub TSA that builds a real (BouncyCastle-generated) RFC 3161
    /// TimeStampToken for every request. Uses a throwaway self-signed cert as the TSA
    /// identity. This lets the engine round-trip a structurally-valid timestamp without
    /// reaching out to a real TSA over the network — keeping the test deterministic and
    /// offline.
    /// </summary>
    /// <remarks>
    /// <c>public</c> (not <c>private</c>) so PadesDocTimeStampTests can reuse the same
    /// stub. A future cleanup is to extract this to <c>tests/.../Internal/</c> alongside
    /// the other shared test fixtures.
    /// </remarks>
    public sealed class InProcessStubTsaProvider : ITimestampAuthorityProvider
    {
        private readonly BcX509Certificate _bcTsaCert;
        private readonly Org.BouncyCastle.Crypto.AsymmetricKeyParameter _bcTsaKey;
        private int _callCount;
        private int _serial;

        public InProcessStubTsaProvider()
        {
            var tsaCert = CreateTsaCert();
            _bcTsaCert = DotNetUtilities.FromX509Certificate(tsaCert);
            using var rsa = tsaCert.GetRSAPrivateKey()!;
            _bcTsaKey = DotNetUtilities.GetRsaKeyPair(rsa).Private;
        }

        /// <summary>
        /// Builds a TSA-capable self-signed cert. BouncyCastle's
        /// <c>TimeStampTokenGenerator</c> calls <c>TspUtil.ValidateCertificate</c> which
        /// rejects any cert that doesn't carry <c>id-kp-timeStamping</c> (1.3.6.1.5.5.7.3.8)
        /// as a <i>critical</i> ExtendedKeyUsage. <see cref="SelfSignedCertificateFactory"/>
        /// produces a documentSigning cert with non-critical EKU, so we can't reuse it.
        /// </summary>
        private static X509Certificate2 CreateTsaCert()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=Stampd Test TSA, O=Stampd",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
                critical: true));

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    // id-kp-timeStamping — required by BC's TSA validator, must be critical
                    // and the ONLY EKU per RFC 3161 §2.3.
                    new Oid("1.3.6.1.5.5.7.3.8"),
                },
                critical: true));

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
                request.PublicKey,
                critical: false));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddDays(30);
            return request.CreateSelfSigned(notBefore, notAfter);
        }

        public int CallCount => _callCount;
        public string Name => "InProcessStub";

        public Task<byte[]> RequestTimestampAsync(
            byte[] dataToTimestamp,
            HashAlgorithmName hashAlgorithm,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);

            var digestOid = MapDigestOid(hashAlgorithm);
            var digest = ComputeHash(dataToTimestamp, hashAlgorithm);

            // Test TSA policy OID — arbitrary but stable across calls.
            const string tsaPolicyOid = "1.3.6.1.4.1.99999.1";

            var tsaCertStore = CollectionUtilities.CreateStore(new List<BcX509Certificate> { _bcTsaCert });

            var generator = new TimeStampTokenGenerator(
                _bcTsaKey,
                _bcTsaCert,
                digestOid,
                tsaPolicyOid);
            generator.SetCertificates(tsaCertStore);

            var requestGen = new TimeStampRequestGenerator();
            var request = requestGen.Generate(digestOid, digest);
            var serial = Interlocked.Increment(ref _serial);
            var token = generator.Generate(request, BigInteger.ValueOf(serial), DateTime.UtcNow);

            return Task.FromResult(token.ToCmsSignedData().GetEncoded(Asn1Encodable.Der));
        }

        private static byte[] ComputeHash(byte[] data, HashAlgorithmName algo)
        {
            using HashAlgorithm hasher = algo.Name switch
            {
                "SHA256" => SHA256.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => throw new NotSupportedException($"Unsupported digest: {algo.Name}"),
            };
            return hasher.ComputeHash(data);
        }

        private static string MapDigestOid(HashAlgorithmName algo) => algo.Name switch
        {
            "SHA256" => "2.16.840.1.101.3.4.2.1",
            "SHA384" => "2.16.840.1.101.3.4.2.2",
            "SHA512" => "2.16.840.1.101.3.4.2.3",
            _ => throw new NotSupportedException($"Unsupported digest: {algo.Name}"),
        };
    }
}
