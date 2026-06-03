using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Security;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.LocalCertificate;

/// <summary>
/// An <see cref="ICryptographicSealingProvider"/> that signs with a locally-held
/// <see cref="X509Certificate2"/> whose private key is directly accessible. Suitable for
/// self-hosted deployments where the certificate is mounted from disk, the OS keystore,
/// or a Kubernetes secret.
/// </summary>
/// <remarks>
/// For production-grade key isolation, prefer one of the HSM-backed providers
/// (<c>Stampd.Crypto.AzureKeyVault</c>, <c>Stampd.Crypto.Vault</c>). The local provider is
/// the first-party "works out of the box" path.
///
/// Signing is delegated to BouncyCastle's <c>RsaDigestSigner</c> rather than .NET's
/// <c>RSA.SignData</c>. Both are PKCS#1 v1.5 with SHA-256 and should produce identical
/// bytes, but the spike verified that BouncyCastle's output is what Adobe's PAdES verifier
/// accepts cleanly in our PdfSharp + BC CMS pipeline.
/// </remarks>
public sealed class LocalCertificateSealingProvider : ICryptographicSealingProvider
{
    private readonly X509Certificate2 _certificate;

    public LocalCertificateSealingProvider(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException(
                "LocalCertificateSealingProvider requires a certificate that includes a private key.",
                nameof(certificate));
        }

        _certificate = certificate;
    }

    /// <inheritdoc />
    public string Name => "LocalCertificate";

    /// <inheritdoc />
    public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_certificate);

    /// <inheritdoc />
    public Task<byte[]> SignAsync(
        byte[] data,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        using var rsa = _certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                "Configured certificate does not expose an RSA private key. ECDSA keys are not yet supported by the local provider.");

        var bcPrivateKey = DotNetUtilities.GetRsaKeyPair(rsa).Private;
        var algorithmName = MapAlgorithm(hashAlgorithm);

        var signer = SignerUtilities.GetSigner(algorithmName);
        signer.Init(forSigning: true, bcPrivateKey);
        signer.BlockUpdate(data, 0, data.Length);
        var signature = signer.GenerateSignature();

        return Task.FromResult(signature);
    }

    private static string MapAlgorithm(HashAlgorithmName hashAlgorithm) => hashAlgorithm.Name switch
    {
        "SHA256" => "SHA256withRSA",
        "SHA384" => "SHA384withRSA",
        "SHA512" => "SHA512withRSA",
        _ => throw new NotSupportedException(
            $"LocalCertificateSealingProvider does not support hash algorithm '{hashAlgorithm.Name}'."),
    };
}
