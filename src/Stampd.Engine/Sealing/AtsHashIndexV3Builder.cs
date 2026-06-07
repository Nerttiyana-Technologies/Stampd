using System.Security.Cryptography;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;

using BcAttribute = Org.BouncyCastle.Asn1.Cms.Attribute;

namespace Stampd.Engine.Sealing;

/// <summary>
/// v1.3 #131 — builds the strict ETSI <c>id-aa-ats-hash-index-v3</c> attribute
/// (OID 1.2.840.113549.1.9.16.2.51) and the matching archive-time-stamp-v3
/// imprint per ETSI TS 101 733 §6.4.3 / RFC 7026. Replaces the v1.2 pragmatic
/// SignerInfo-DER imprint with a wire-compliant computation that locks in
/// exactly which certs, CRLs, and existing unsigned attributes the archive TST
/// covers, so any future addition to <c>signedData</c> can't silently
/// invalidate the timestamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the hash index matters.</b> CMS <c>signedData</c> can grow over time —
/// adopters may insert long-term CRLs, additional cert chains, or new unsigned
/// attributes after the archive timestamp is issued. Without the hash index,
/// a verifier in 2035 sees a TST imprint that "covers" the modern CMS state and
/// can't tell which content the TSA actually witnessed in 2026. The hash index
/// is the spec's answer: it freezes the membership of each list by hash and the
/// imprint is computed over those frozen hashes plus the signer fields.
/// </para>
/// <para>
/// <b>BouncyCastle.NET 2.6.2 has no built-in helper.</b> The Java port does
/// (<c>CMSSignedDataExt.calculateArchiveTimestampV3Hash</c>); the .NET port
/// does not, so this class constructs the attribute and the imprint by hand
/// from BC's ASN.1 primitives. Kept as a pure static helper — no DI, no I/O,
/// no async — so unit tests can pin every wire byte.
/// </para>
/// </remarks>
internal static class AtsHashIndexV3Builder
{
    /// <summary>
    /// RFC 7026 — <c>id-aa-ATSHashIndex-v3</c> identifier for the hash index attribute.
    /// </summary>
    public static readonly DerObjectIdentifier IdAaAtsHashIndexV3 =
        new("1.2.840.113549.1.9.16.2.51");

    /// <summary>
    /// Builds the ats-hash-index-v3 attribute over the supplied CMS material. The
    /// attribute is intended to be appended to the signer's unsigned attributes
    /// <i>before</i> the archive timestamp is requested — the imprint hash then
    /// includes this attribute, locking the index into the TST.
    /// </summary>
    /// <param name="certificates">
    /// Every certificate present in <c>signedData.certificates</c>. Order doesn't
    /// matter for verifier correctness (the spec sorts no entries); we preserve
    /// the input order for determinism.
    /// </param>
    /// <param name="crls">
    /// Every CRL present in <c>signedData.crls</c>. Stampd embeds revocation in
    /// the PDF DSS, not in CMS, so this is typically empty — but we keep the
    /// parameter so adopters who DO ship CRLs inside CMS get a correct attribute.
    /// </param>
    /// <param name="existingUnsignedAttributeValues">
    /// The DER of each existing unsigned-attribute <i>value</i> (the contents of
    /// each Attribute's SET-OF AttributeValue) at the moment the archive TST is
    /// being requested. For a B-T → B-LTA transition this is exactly the
    /// signature-time-stamp-token attribute value(s).
    /// </param>
    /// <param name="hashAlgorithm">
    /// Hash algorithm used for every entry in the index AND for the imprint.
    /// Identified inside the attribute's <c>hashIndAlgorithm</c> field.
    /// </param>
    public static BcAttribute Build(
        IReadOnlyList<byte[]> certificates,
        IReadOnlyList<byte[]> crls,
        IReadOnlyList<byte[]> existingUnsignedAttributeValues,
        HashAlgorithmName hashAlgorithm)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(crls);
        ArgumentNullException.ThrowIfNull(existingUnsignedAttributeValues);

        var algorithmIdentifier = BuildAlgorithmIdentifier(hashAlgorithm);

        var certsIndex = HashEach(certificates, hashAlgorithm);
        var crlsIndex = HashEach(crls, hashAlgorithm);
        var unsignedAttrsIndex = HashEach(existingUnsignedAttributeValues, hashAlgorithm);

        // ATSHashIndex-v3 ::= SEQUENCE {
        //   hashIndAlgorithm            AlgorithmIdentifier DEFAULT id-sha256,
        //   certificatesHashIndex       SEQUENCE OF OCTET STRING,
        //   crlsHashIndex               SEQUENCE OF OCTET STRING,
        //   unsignedAttrValuesHashIndex SEQUENCE OF OCTET STRING }
        //
        // The DEFAULT clause means SHA-256 callers MAY omit the algorithm field;
        // we emit it unconditionally for clarity, which is spec-legal and what
        // every interoperable implementation (BC Java, EU DSS) does in practice.
        var atsHashIndexSequence = new DerSequence(
            algorithmIdentifier,
            certsIndex,
            crlsIndex,
            unsignedAttrsIndex);

        // Disambiguate the DerSet ctor — BC.NET 2.6 has both DerSet(Asn1Encodable) and
        // DerSet(IReadOnlyCollection<Asn1Encodable>), and DerSequence implements both,
        // so we cast to the scalar-element shape we want.
        return new BcAttribute(IdAaAtsHashIndexV3, new DerSet((Asn1Encodable)atsHashIndexSequence));
    }

    /// <summary>
    /// Convenience overload that pulls the cert and CRL DER bytes directly out of a
    /// <see cref="CmsSignedData"/>. Adopters with custom signed-data assembly can
    /// call <see cref="Build(IReadOnlyList{byte[]}, IReadOnlyList{byte[]}, IReadOnlyList{byte[]}, HashAlgorithmName)"/>
    /// directly.
    /// </summary>
    public static BcAttribute Build(
        CmsSignedData signedData,
        IReadOnlyList<byte[]> existingUnsignedAttributeValues,
        HashAlgorithmName hashAlgorithm)
    {
        ArgumentNullException.ThrowIfNull(signedData);

        var certificates = ExtractCertificateDer(signedData);
        var crls = ExtractCrlDer(signedData);

        return Build(certificates, crls, existingUnsignedAttributeValues, hashAlgorithm);
    }

    /// <summary>
    /// Computes the archive-time-stamp-v3 imprint per ETSI TS 101 733 §6.4.3. The
    /// returned bytes are what gets handed to the TSA — the TSA will sign a hash
    /// of these bytes (via its TimeStampReq's messageImprint field), producing a
    /// TST that uniquely commits to (eContent || all signer fields || ats-hash-index).
    /// </summary>
    /// <param name="eContentType">DER of <c>signedData.encapContentInfo.eContentType</c>.</param>
    /// <param name="eContent">
    /// Encapsulated <c>eContent</c> bytes (just the OCTET STRING contents, not the
    /// tag/length), or null for a detached signature.
    /// </param>
    /// <param name="signers">Per-signer field bundle, in <c>signerInfos</c> order.</param>
    /// <param name="atsHashIndexAttributeDer">
    /// DER of the entire ats-hash-index-v3 Attribute (oid + values) that's being
    /// added to the signer's unsigned attributes alongside the archive TST.
    /// </param>
    /// <param name="hashAlgorithm">Hash to apply over the concatenation.</param>
    public static byte[] ComputeImprint(
        byte[] eContentType,
        byte[]? eContent,
        IReadOnlyList<SignerFieldBundle> signers,
        byte[] atsHashIndexAttributeDer,
        HashAlgorithmName hashAlgorithm)
    {
        ArgumentNullException.ThrowIfNull(eContentType);
        ArgumentNullException.ThrowIfNull(signers);
        ArgumentNullException.ThrowIfNull(atsHashIndexAttributeDer);

        using var ms = new MemoryStream();

        ms.Write(eContentType);
        if (eContent is not null)
        {
            ms.Write(eContent);
        }

        // Per spec, signers contribute in signerInfos declaration order. Each
        // bundle holds the DER of fields the TSA needs to commit to: signer
        // identity, digest + signature algorithms, signed-attribute set, and
        // the signature value itself.
        foreach (var bundle in signers)
        {
            ms.Write(bundle.SignerIdentifierDer);
            ms.Write(bundle.DigestAlgorithmDer);
            ms.Write(bundle.SignedAttributesDer);
            ms.Write(bundle.SignatureAlgorithmDer);
            ms.Write(bundle.SignatureDer);
        }

        ms.Write(atsHashIndexAttributeDer);

        using var hasher = CreateHasher(hashAlgorithm);
        return hasher.ComputeHash(ms.ToArray());
    }

    /// <summary>
    /// DER bundle of the SignerInfo fields the archive-time-stamp-v3 imprint must
    /// cover. One per signer in the CMS signed-data.
    /// </summary>
    public sealed record SignerFieldBundle(
        byte[] SignerIdentifierDer,
        byte[] DigestAlgorithmDer,
        byte[] SignedAttributesDer,
        byte[] SignatureAlgorithmDer,
        byte[] SignatureDer);

    /// <summary>
    /// Pulls the DER bytes of every certificate in <c>signedData.certificates</c>. We
    /// use the raw DER so the verifier can re-derive identical hashes by extracting
    /// each cert and hashing it themselves.
    /// </summary>
    private static List<byte[]> ExtractCertificateDer(CmsSignedData signedData)
    {
        var list = new List<byte[]>();
        var store = signedData.GetCertificates();
        if (store is null) return list;

        foreach (var holder in store.EnumerateMatches(selector: null))
        {
            list.Add(holder.GetEncoded());
        }
        return list;
    }

    /// <summary>
    /// Pulls the DER bytes of every CRL in <c>signedData.crls</c>. Empty in the
    /// typical Stampd flow (revocation lives in the PDF DSS, not in CMS).
    /// </summary>
    private static List<byte[]> ExtractCrlDer(CmsSignedData signedData)
    {
        var list = new List<byte[]>();
        var store = signedData.GetCrls();
        if (store is null) return list;

        foreach (var holder in store.EnumerateMatches(selector: null))
        {
            list.Add(holder.GetEncoded());
        }
        return list;
    }

    private static DerSequence HashEach(
        IReadOnlyList<byte[]> items,
        HashAlgorithmName hashAlgorithm)
    {
        var elements = new Asn1EncodableVector();
        using var hasher = CreateHasher(hashAlgorithm);
        foreach (var item in items)
        {
            hasher.Initialize();
            elements.Add(new DerOctetString(hasher.ComputeHash(item)));
        }
        return new DerSequence(elements);
    }

    private static AlgorithmIdentifier BuildAlgorithmIdentifier(HashAlgorithmName hashAlgorithm)
        => hashAlgorithm.Name switch
        {
            "SHA256" => new AlgorithmIdentifier(NistObjectIdentifiers.IdSha256, DerNull.Instance),
            "SHA384" => new AlgorithmIdentifier(NistObjectIdentifiers.IdSha384, DerNull.Instance),
            "SHA512" => new AlgorithmIdentifier(NistObjectIdentifiers.IdSha512, DerNull.Instance),
            _ => throw new NotSupportedException(
                $"AtsHashIndexV3Builder does not support hash algorithm '{hashAlgorithm.Name}'."),
        };

    private static HashAlgorithm CreateHasher(HashAlgorithmName hashAlgorithm)
        => hashAlgorithm.Name switch
        {
            "SHA256" => SHA256.Create(),
            "SHA384" => SHA384.Create(),
            "SHA512" => SHA512.Create(),
            _ => throw new NotSupportedException(
                $"AtsHashIndexV3Builder does not support hash algorithm '{hashAlgorithm.Name}'."),
        };
}
