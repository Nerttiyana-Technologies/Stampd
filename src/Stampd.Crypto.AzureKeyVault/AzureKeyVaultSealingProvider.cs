using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Azure.Core;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys.Cryptography;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.AzureKeyVault;

/// <summary>
/// Signs documents using a key held in Azure Key Vault (or Managed HSM). The private key
/// never leaves the vault — we hash the data-to-be-signed locally, then submit the digest
/// for remote signing via <c>CryptographyClient.Sign</c>.
/// </summary>
/// <remarks>
/// <para>
/// The signer certificate (public part + chain) is loaded from Key Vault Certificates —
/// either the same vault holding the key, or a different one. Configure both via
/// <see cref="AzureKeyVaultSealingOptions"/>.
/// </para>
/// <para>
/// Auth uses <see cref="TokenCredential"/> — typically <c>DefaultAzureCredential</c> which
/// chains environment variables, Managed Identity, Azure CLI, etc. Adopters can substitute
/// any TokenCredential (workload identity federation, client cert, etc.).
/// </para>
/// </remarks>
public sealed class AzureKeyVaultSealingProvider : ICryptographicSealingProvider
{
    private readonly AzureKeyVaultSealingOptions _options;
    private readonly TokenCredential _credential;
    private readonly Lazy<Task<X509Certificate2>> _certificate;
    private readonly Lazy<CryptographyClient> _cryptoClient;

    public AzureKeyVaultSealingProvider(AzureKeyVaultSealingOptions options, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        options.Validate();

        _options = options;
        _credential = credential;

        _certificate = new Lazy<Task<X509Certificate2>>(LoadCertificateAsync);
        _cryptoClient = new Lazy<CryptographyClient>(
            () => new CryptographyClient(_options.KeyIdentifier!, _credential));
    }

    /// <inheritdoc />
    public string Name => "AzureKeyVault";

    /// <inheritdoc />
    public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
        => _certificate.Value;

    /// <inheritdoc />
    public async Task<byte[]> SignAsync(
        byte[] data,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Key Vault's SignAsync wants a pre-computed digest, not the raw data.
        var digest = ComputeDigest(data, hashAlgorithm);
        var algorithm = MapToKeyVaultSignatureAlgorithm(hashAlgorithm);

        var result = await _cryptoClient.Value
            .SignAsync(algorithm, digest, cancellationToken)
            .ConfigureAwait(false);

        return result.Signature;
    }

    private async Task<X509Certificate2> LoadCertificateAsync()
    {
        var certClient = new CertificateClient(_options.CertificateVaultUri!, _credential);
        var response = await certClient
            .DownloadCertificateAsync(_options.CertificateName!)
            .ConfigureAwait(false);
        // DownloadCertificate returns an X509Certificate2; for HSM-backed keys it carries
        // only the public part (no exportable private key).
        return response.Value;
    }

    private static byte[] ComputeDigest(byte[] data, HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256) return SHA256.HashData(data);
        if (algorithm == HashAlgorithmName.SHA384) return SHA384.HashData(data);
        if (algorithm == HashAlgorithmName.SHA512) return SHA512.HashData(data);
        throw new NotSupportedException($"Unsupported hash algorithm '{algorithm.Name}'.");
    }

    private static SignatureAlgorithm MapToKeyVaultSignatureAlgorithm(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256) return SignatureAlgorithm.RS256;
        if (algorithm == HashAlgorithmName.SHA384) return SignatureAlgorithm.RS384;
        if (algorithm == HashAlgorithmName.SHA512) return SignatureAlgorithm.RS512;
        throw new NotSupportedException($"Unsupported hash algorithm '{algorithm.Name}'.");
    }
}
