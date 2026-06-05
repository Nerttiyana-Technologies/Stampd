using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;

using Stampd.Core.Entities;
using Stampd.Core.Sealing;

using BcAttribute = Org.BouncyCastle.Asn1.Cms.Attribute;
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace Stampd.Engine.Sealing;

/// <summary>
/// Builds the PKCS#7 / CMS SignedData blob that PDFsharp embeds into the PDF signature
/// dictionary's <c>/Contents</c> entry. Picks one of two signing paths automatically:
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct path (LocalCertificate)</b>: cert exposes a usable RSA private key.
/// BouncyCastle's built-in <c>Asn1SignatureFactory(algorithmName, bcPrivateKey)</c>
/// signs the CMS attributes directly. This is the proven-with-Adobe path the spike
/// validated.
/// </para>
/// <para>
/// <b>HSM path</b>: cert has no local private key (Azure Key Vault, HashiCorp Vault,
/// OpenBao, hardware HSM). The CMS pipeline streams the signed-attributes bytes through
/// <see cref="ProviderSignatureFactory"/>, which calls back into the provider's async
/// <see cref="ICryptographicSealingProvider.SignAsync"/>. The private key never leaves
/// the HSM.
/// </para>
/// <para>
/// PAdES B-T (RFC 3161 timestamp) is added on top when an
/// <see cref="ITimestampAuthorityProvider"/> is configured.
/// </para>
/// </remarks>
internal sealed class PadesCmsBuilder
{
    // RFC 5816 / ETSI TS 101 733 §6.4.3 — archive-time-stamp-v3 unsigned attribute.
    // Wraps a TSA token that anchors the entire B-LT signature + DSS to a future point
    // in time, so the signature remains verifiable after the signer cert expires.
    private static readonly DerObjectIdentifier IdAaEtsArchiveTimestampV3 =
        new("1.2.840.113549.1.9.16.2.48");

    private readonly ICryptographicSealingProvider _provider;
    private readonly HashAlgorithmName _hashAlgorithm;
    private readonly ITimestampAuthorityProvider? _timestampAuthority;
    private readonly PAdESLevel _targetLevel;
    // Captured at BuildAsync entry so EmbedArchiveTimestampAsync (called downstream)
    // can substitute it for the absent eContent when computing the strict
    // archive-time-stamp-v3 imprint (per ETSI TS 101 733 §6.4.3, detached signatures
    // use the externally-signed data in place of eContent).
    private byte[]? _lastToBeSigned;

    public PadesCmsBuilder(
        ICryptographicSealingProvider provider,
        HashAlgorithmName hashAlgorithm,
        ITimestampAuthorityProvider? timestampAuthority = null,
        PAdESLevel targetLevel = PAdESLevel.BT)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _hashAlgorithm = hashAlgorithm;
        _timestampAuthority = timestampAuthority;
        _targetLevel = targetLevel;
    }

    public async Task<byte[]> BuildAsync(byte[] toBeSigned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toBeSigned);
        _lastToBeSigned = toBeSigned;

        var dotnetCertificate = await _provider
            .GetSigningCertificateAsync(cancellationToken)
            .ConfigureAwait(false);

        BcX509Certificate bcCertificate = DotNetUtilities.FromX509Certificate(dotnetCertificate);
        var algorithmName = MapAlgorithm(_hashAlgorithm);

        ISignatureFactory signatureFactory = SelectSignatureFactory(dotnetCertificate, algorithmName);

        var generator = new CmsSignedDataGenerator();

        var signerInfoGenerator = new SignerInfoGeneratorBuilder()
            .Build(signatureFactory, bcCertificate);

        generator.AddSignerInfoGenerator(signerInfoGenerator);

        var certStore = CollectionUtilities.CreateStore(
            new List<BcX509Certificate> { bcCertificate });
        generator.AddCertificates(certStore);

        var cmsContent = new CmsProcessableByteArray(toBeSigned);

        var signedData = generator.Generate(cmsContent, encapsulate: false);

        if (_timestampAuthority is not null)
        {
            signedData = await EmbedSignatureTimestampAsync(signedData, cancellationToken)
                .ConfigureAwait(false);

            // B-LTA layers an archive timestamp on top of the B-T signature TST. The
            // archive TST is anchored to a separate (typically later) TSA assertion and
            // protects the signature + signature-TST against the eventual expiry of the
            // signer's certificate and the original TSA's certificate.
            if (_targetLevel == PAdESLevel.BLTA)
            {
                signedData = await EmbedArchiveTimestampAsync(signedData, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // PAdES requires DER (definite-length). BC defaults to BER indefinite-length.
        return signedData.GetEncoded(Asn1Encodable.Der);
    }

    /// <summary>
    /// Chooses the direct (local key) path if the cert exposes an RSA private key, or
    /// the HSM-compatible callback path otherwise.
    /// </summary>
    private ISignatureFactory SelectSignatureFactory(
        X509Certificate2 certificate,
        string algorithmName)
    {
        if (!certificate.HasPrivateKey)
        {
            // Cert is public-only — must route signing through the provider's SignAsync.
            return new ProviderSignatureFactory(_provider, _hashAlgorithm);
        }

        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null)
        {
            // HasPrivateKey lied (e.g. ECDSA cert) — fall back to HSM path.
            return new ProviderSignatureFactory(_provider, _hashAlgorithm);
        }

        var bcPrivateKey = DotNetUtilities.GetRsaKeyPair(rsa).Private;
        return new Asn1SignatureFactory(algorithmName, bcPrivateKey);
    }

    private async Task<CmsSignedData> EmbedSignatureTimestampAsync(
        CmsSignedData signedData,
        CancellationToken cancellationToken)
    {
        var existingSigners = signedData.GetSignerInfos().GetSigners();
        var updatedSigners = new List<SignerInformation>();

        foreach (SignerInformation signer in existingSigners)
        {
            var signatureBytes = signer.GetSignature();

            var tstBytes = await _timestampAuthority!
                .RequestTimestampAsync(signatureBytes, _hashAlgorithm, cancellationToken)
                .ConfigureAwait(false);

            var tstAsn1 = Asn1Object.FromByteArray(tstBytes);

            var timestampAttribute = new BcAttribute(
                PkcsObjectIdentifiers.IdAASignatureTimeStampToken,
                new DerSet(tstAsn1));

            var unsignedAttrs = new AttributeTable(new DerSet(timestampAttribute));

            var updatedSigner = SignerInformation.ReplaceUnsignedAttributes(signer, unsignedAttrs);
            updatedSigners.Add(updatedSigner);
        }

        var newSignerStore = new SignerInformationStore(updatedSigners);
        return CmsSignedData.ReplaceSigners(signedData, newSignerStore);
    }

    /// <summary>
    /// Embeds an archive timestamp (id-aa-ets-archiveTimestampV3) as a second unsigned
    /// attribute on each signer. This is the PAdES B-LTA layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>v1.3 #131 — strict ETSI ATSHashIndexV3 imprint.</b> Per ETSI TS 101 733 §6.4.3
    /// (and RFC 7026), the archive-time-stamp-v3 imprint is computed over a precisely
    /// ordered concatenation of CMS material plus a freshly built
    /// <c>id-aa-ats-hash-index-v3</c> attribute. The hash index locks in which certs,
    /// CRLs, and unsigned attributes the TST witnessed at archive time — without it,
    /// adopters who later add long-term revocation data could silently invalidate the
    /// archive timestamp. v1.2 used a pragmatic SignerInfo-DER imprint as a stop-gap;
    /// this method now implements the spec-compliant computation.
    /// </para>
    /// <para>
    /// The resulting signer has TWO new unsigned attributes after this method runs:
    /// the <c>ats-hash-index-v3</c> attribute (so the verifier can re-derive the same
    /// imprint) and the <c>archive-time-stamp-v3</c> attribute carrying the TST.
    /// </para>
    /// </remarks>
    private async Task<CmsSignedData> EmbedArchiveTimestampAsync(
        CmsSignedData signedData,
        CancellationToken cancellationToken)
    {
        // Detached PAdES: eContent is absent from the CMS, so the spec says we use
        // the external signed data (the PDF /ByteRange-covered bytes) in its place.
        // _lastToBeSigned was captured at the top of BuildAsync.
        var toBeSigned = _lastToBeSigned
            ?? throw new InvalidOperationException(
                "PadesCmsBuilder.EmbedArchiveTimestampAsync requires the toBeSigned bytes captured by BuildAsync.");

        // eContentType = id-data (1.2.840.113549.1.7.1) for PAdES detached signatures.
        // The spec requires its full DER encoding (tag + length + OID body) — we get
        // that from BC's PkcsObjectIdentifiers.Data, which is canonical.
        var eContentTypeDer = PkcsObjectIdentifiers.Data.GetEncoded(Asn1Encodable.Der);

        var existingSigners = signedData.GetSignerInfos().GetSigners();
        var updatedSigners = new List<SignerInformation>();

        foreach (SignerInformation signer in existingSigners)
        {
            // 1) Snapshot the existing unsigned-attribute VALUES (per-Attribute SET-OF
            //    AttributeValue, DER) so the hash index covers exactly what's there
            //    before we append. For B-T → B-LTA this is the signature-TST attribute.
            var existingUnsignedValueDer = ExtractUnsignedAttributeValueDer(signer);

            // 2) Build the ats-hash-index-v3 attribute over (certs, CRLs, existing
            //    unsigned attr values). This MUST happen before imprint computation —
            //    the attribute's own DER is part of what gets hashed.
            var atsHashIndexAttribute = AtsHashIndexV3Builder.Build(
                signedData,
                existingUnsignedValueDer,
                _hashAlgorithm);
            var atsHashIndexAttributeDer = atsHashIndexAttribute.GetEncoded(Asn1Encodable.Der);

            // 3) Pull the signer-info field bundle the spec requires: SID, digestAlgo,
            //    signedAttrs (SET-OF encoding, not the IMPLICIT [0] wire form), the
            //    signature algorithm, and the signature value itself. Each is DER'd
            //    so byte equality holds across re-parses.
            var signerBundle = BuildSignerFieldBundle(signer);

            // 4) Compute the imprint and ask the TSA to sign it. The TSA returns an
            //    RFC 3161 TimeStampToken whose messageImprint hash equals SHA-?(imprint).
            //    We pass the raw imprint bytes; the TSA provider hashes them internally
            //    using _hashAlgorithm (same convention as the signature TST path).
            var imprintBytes = AtsHashIndexV3Builder.ComputeImprint(
                eContentTypeDer,
                toBeSigned,
                [signerBundle],
                atsHashIndexAttributeDer,
                _hashAlgorithm);

            var tstBytes = await _timestampAuthority!
                .RequestTimestampAsync(imprintBytes, _hashAlgorithm, cancellationToken)
                .ConfigureAwait(false);

            var tstAsn1 = Asn1Object.FromByteArray(tstBytes);

            // 5) Append BOTH attributes to the signer's unsigned attrs. Order doesn't
            //    matter to verifiers but we keep ats-hash-index-v3 first so a
            //    casual inspection finds the index right next to the TST it locks in.
            //    Don't ReplaceUnsignedAttributes — that would drop the B-T timestamp
            //    and degrade back to B-B.
            var existingUnsigned = signer.UnsignedAttributes
                ?? new AttributeTable(new Asn1EncodableVector());

            var withHashIndex = existingUnsigned.Add(
                AtsHashIndexV3Builder.IdAaAtsHashIndexV3,
                atsHashIndexAttribute.AttrValues[0]);

            var withArchiveTst = withHashIndex.Add(IdAaEtsArchiveTimestampV3, tstAsn1);

            var updatedSigner = SignerInformation.ReplaceUnsignedAttributes(signer, withArchiveTst);
            updatedSigners.Add(updatedSigner);
        }

        var newSignerStore = new SignerInformationStore(updatedSigners);
        return CmsSignedData.ReplaceSigners(signedData, newSignerStore);
    }

    /// <summary>
    /// Returns the DER bytes of each existing unsigned attribute's <i>value set</i>
    /// (the contents of the Attribute's <c>SET OF AttributeValue</c>). The ats-hash-index
    /// hashes these to lock in what was present at archive time.
    /// </summary>
    private static List<byte[]> ExtractUnsignedAttributeValueDer(SignerInformation signer)
    {
        var values = new List<byte[]>();
        var unsigned = signer.UnsignedAttributes;
        if (unsigned is null) return values;

        // AttributeTable.ToAsn1EncodableVector iterates Attribute objects in their
        // declaration order. For each Attribute, hash its AttrValues SET — that's
        // the actual content the spec wants frozen.
        foreach (var encodable in unsigned.ToAsn1EncodableVector())
        {
            if (encodable is BcAttribute attribute)
            {
                values.Add(attribute.AttrValues.GetEncoded(Asn1Encodable.Der));
            }
        }
        return values;
    }

    /// <summary>
    /// Extracts the DER of each SignerInfo field the archive-time-stamp-v3 imprint
    /// must commit to: SID, digest algorithm, signed attributes (as a SET OF Attribute,
    /// NOT the IMPLICIT [0]-tagged wire form), signature algorithm, signature value.
    /// </summary>
    private static AtsHashIndexV3Builder.SignerFieldBundle BuildSignerFieldBundle(
        SignerInformation signer)
    {
        // BC.NET 2.6 renamed the SignerInfo accessors away from the RFC-prose names
        // ("AuthenticatedAttributes", "DigestEncryptionAlgorithm", "EncryptedDigest")
        // to PKIX-style short names ("SignedAttrs", "SignatureAlgorithm", "Signature").
        // ToSignerInfo() also got obsoleted in favor of the SignerInfo property.
        var signerInfo = signer.SignerInfo;

        // The signed-attributes field rides in SignerInfo as [0] IMPLICIT — the
        // SignerInfo struct re-tags the SET OF Attribute. For the imprint we want the
        // canonical SET-OF encoding (tag 0x31), matching how the signer's message
        // digest was originally computed in RFC 5652 §5.4. BC's SignedAttrs accessor
        // returns the Asn1Set; .GetEncoded() emits 0x31, not 0xA0.
        var signedAttrsDer = signerInfo.SignedAttrs is { } signedAttrs
            ? signedAttrs.GetEncoded(Asn1Encodable.Der)
            // The spec assumes signed attrs are present (they always are for PAdES,
            // since the content-type and message-digest attrs are mandatory) — but
            // guard against malformed CMS by emitting an empty SET in that case.
            : new DerSet().GetEncoded(Asn1Encodable.Der);

        return new AtsHashIndexV3Builder.SignerFieldBundle(
            SignerIdentifierDer: signerInfo.SignerID.GetEncoded(Asn1Encodable.Der),
            DigestAlgorithmDer: signerInfo.DigestAlgorithm.GetEncoded(Asn1Encodable.Der),
            SignedAttributesDer: signedAttrsDer,
            SignatureAlgorithmDer: signerInfo.SignatureAlgorithm.GetEncoded(Asn1Encodable.Der),
            SignatureDer: signerInfo.Signature.GetEncoded(Asn1Encodable.Der));
    }

    private static string MapAlgorithm(HashAlgorithmName hashAlgorithm) => hashAlgorithm.Name switch
    {
        "SHA256" => "SHA256withRSA",
        "SHA384" => "SHA384withRSA",
        "SHA512" => "SHA512withRSA",
        _ => throw new NotSupportedException(
            $"PadesCmsBuilder does not support hash algorithm '{hashAlgorithm.Name}'."),
    };
}
