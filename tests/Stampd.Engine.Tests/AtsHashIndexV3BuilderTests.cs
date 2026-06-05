using System.Security.Cryptography;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;

using Stampd.Engine.Sealing;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// Pure-function unit tests for <see cref="AtsHashIndexV3Builder"/>. These run
/// independent of the engine, the PDF pipeline, and BouncyCastle's CMS layer —
/// they exist to pin the wire shape of the ats-hash-index-v3 attribute and the
/// imprint computation against known inputs, so future BC upgrades or refactors
/// can't silently drift the bytes Stampd hands to a TSA.
/// </summary>
public sealed class AtsHashIndexV3BuilderTests
{
    private const string AtsHashIndexV3Oid = "1.2.840.113549.1.9.16.2.51";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string Sha384Oid = "2.16.840.1.101.3.4.2.2";

    [Fact]
    public void Build_WithSha256_EmitsAttributeWithCorrectOidAndAlgorithm()
    {
        // Three certs, two CRLs, one existing unsigned-attr value — just enough
        // to verify per-category counts land in the right SEQUENCE positions.
        var certs = new[] { Bytes(0x01), Bytes(0x02), Bytes(0x03) };
        var crls = new[] { Bytes(0x10), Bytes(0x11) };
        var unsignedAttrValues = new[] { Bytes(0x20) };

        var attribute = AtsHashIndexV3Builder.Build(certs, crls, unsignedAttrValues, HashAlgorithmName.SHA256);

        Assert.Equal(AtsHashIndexV3Oid, attribute.AttrType.Id);

        var sequence = (Asn1Sequence)attribute.AttrValues[0];
        Assert.Equal(4, sequence.Count); // algorithm + 3 hash indexes

        var algorithm = AlgorithmIdentifier.GetInstance(sequence[0]);
        Assert.Equal(Sha256Oid, algorithm.Algorithm.Id);

        var certsSeq = (Asn1Sequence)sequence[1];
        var crlsSeq = (Asn1Sequence)sequence[2];
        var unsignedAttrsSeq = (Asn1Sequence)sequence[3];

        Assert.Equal(3, certsSeq.Count);
        Assert.Equal(2, crlsSeq.Count);
        Assert.Single(unsignedAttrsSeq);
    }

    [Fact]
    public void Build_HashesEachInputUnderTheChosenAlgorithm()
    {
        // Hand-craft a known input and verify the OCTET STRING content matches
        // SHA-256(input). This is the load-bearing invariant — verifiers re-derive
        // the same hashes by extracting each cert and hashing it themselves.
        var cert = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var expectedHash = SHA256.HashData(cert);

        var attribute = AtsHashIndexV3Builder.Build(
            certificates: [cert],
            crls: [],
            existingUnsignedAttributeValues: [],
            HashAlgorithmName.SHA256);

        var sequence = (Asn1Sequence)attribute.AttrValues[0];
        var certsSeq = (Asn1Sequence)sequence[1];
        var firstCertHash = ((DerOctetString)certsSeq[0]).GetOctets();

        Assert.Equal(expectedHash, firstCertHash);
    }

    [Fact]
    public void Build_WithSha384_EmitsCorrectAlgorithmIdentifier()
    {
        var attribute = AtsHashIndexV3Builder.Build(
            certificates: [Bytes(0x42)],
            crls: [],
            existingUnsignedAttributeValues: [],
            HashAlgorithmName.SHA384);

        var sequence = (Asn1Sequence)attribute.AttrValues[0];
        var algorithm = AlgorithmIdentifier.GetInstance(sequence[0]);
        Assert.Equal(Sha384Oid, algorithm.Algorithm.Id);

        // SHA-384 output is 48 bytes — the hash entry should be exactly that long.
        var certsSeq = (Asn1Sequence)sequence[1];
        var hashBytes = ((DerOctetString)certsSeq[0]).GetOctets();
        Assert.Equal(48, hashBytes.Length);
    }

    [Fact]
    public void Build_WithEmptyInputs_StillEmitsAllFourFields()
    {
        // The spec is explicit that empty SEQUENCES are valid for each hash index
        // — adopters who don't ship CRLs or unsigned attrs at archive time must
        // still emit an empty SEQUENCE OF, not a missing field.
        var attribute = AtsHashIndexV3Builder.Build(
            certificates: [],
            crls: [],
            existingUnsignedAttributeValues: [],
            HashAlgorithmName.SHA256);

        var sequence = (Asn1Sequence)attribute.AttrValues[0];
        Assert.Equal(4, sequence.Count);
        Assert.Empty((Asn1Sequence)sequence[1]);
        Assert.Empty((Asn1Sequence)sequence[2]);
        Assert.Empty((Asn1Sequence)sequence[3]);
    }

    [Fact]
    public void ComputeImprint_IsDeterministicForSameInputs()
    {
        // The imprint is what gets handed to the TSA — same inputs must always
        // produce the same hash, otherwise verifiers can't re-derive it.
        var eContentType = Bytes(0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x01);
        var eContent = Bytes(0xDE, 0xAD, 0xBE, 0xEF);
        var atsAttribute = Bytes(0x30, 0x01, 0x00);

        var bundle = new AtsHashIndexV3Builder.SignerFieldBundle(
            SignerIdentifierDer: Bytes(0xA0),
            DigestAlgorithmDer: Bytes(0xA1),
            SignedAttributesDer: Bytes(0xA2),
            SignatureAlgorithmDer: Bytes(0xA3),
            SignatureDer: Bytes(0xA4));

        var first = AtsHashIndexV3Builder.ComputeImprint(
            eContentType, eContent, [bundle], atsAttribute, HashAlgorithmName.SHA256);
        var second = AtsHashIndexV3Builder.ComputeImprint(
            eContentType, eContent, [bundle], atsAttribute, HashAlgorithmName.SHA256);

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length); // SHA-256 output
    }

    [Fact]
    public void ComputeImprint_IncludesEContentTypeInTheHash()
    {
        // Sensitivity test: changing eContentType while holding everything else
        // constant must produce a different imprint, otherwise we'd accept a
        // detached signature over the wrong content type.
        var atsAttribute = Bytes(0x30, 0x00);
        var bundle = new AtsHashIndexV3Builder.SignerFieldBundle(
            SignerIdentifierDer: Bytes(),
            DigestAlgorithmDer: Bytes(),
            SignedAttributesDer: Bytes(),
            SignatureAlgorithmDer: Bytes(),
            SignatureDer: Bytes());

        var a = AtsHashIndexV3Builder.ComputeImprint(
            Bytes(0x01), null, [bundle], atsAttribute, HashAlgorithmName.SHA256);
        var b = AtsHashIndexV3Builder.ComputeImprint(
            Bytes(0x02), null, [bundle], atsAttribute, HashAlgorithmName.SHA256);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeImprint_IncludesAtsHashIndexAttributeInTheHash()
    {
        // The whole point of the hash index is that it gets baked into the TST
        // imprint. Verify that swapping the attribute changes the output.
        var bundle = new AtsHashIndexV3Builder.SignerFieldBundle(
            SignerIdentifierDer: Bytes(),
            DigestAlgorithmDer: Bytes(),
            SignedAttributesDer: Bytes(),
            SignatureAlgorithmDer: Bytes(),
            SignatureDer: Bytes());

        var eContentType = Bytes(0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x01);

        var a = AtsHashIndexV3Builder.ComputeImprint(
            eContentType, null, [bundle], Bytes(0xAA), HashAlgorithmName.SHA256);
        var b = AtsHashIndexV3Builder.ComputeImprint(
            eContentType, null, [bundle], Bytes(0xBB), HashAlgorithmName.SHA256);

        Assert.NotEqual(a, b);
    }

    private static byte[] Bytes(params byte[] bytes) => bytes;
}
