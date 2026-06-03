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
    private readonly ICryptographicSealingProvider _provider;
    private readonly HashAlgorithmName _hashAlgorithm;
    private readonly ITimestampAuthorityProvider? _timestampAuthority;

    public PadesCmsBuilder(
        ICryptographicSealingProvider provider,
        HashAlgorithmName hashAlgorithm,
        ITimestampAuthorityProvider? timestampAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _hashAlgorithm = hashAlgorithm;
        _timestampAuthority = timestampAuthority;
    }

    public async Task<byte[]> BuildAsync(byte[] toBeSigned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toBeSigned);

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

    private static string MapAlgorithm(HashAlgorithmName hashAlgorithm) => hashAlgorithm.Name switch
    {
        "SHA256" => "SHA256withRSA",
        "SHA384" => "SHA384withRSA",
        "SHA512" => "SHA512withRSA",
        _ => throw new NotSupportedException(
            $"PadesCmsBuilder does not support hash algorithm '{hashAlgorithm.Name}'."),
    };
}
