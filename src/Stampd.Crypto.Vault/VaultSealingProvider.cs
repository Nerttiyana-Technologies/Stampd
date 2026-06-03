using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Stampd.Core.Sealing;

using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;
using VaultSharp.V1.AuthMethods.Kubernetes;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.Transit;

namespace Stampd.Crypto.Vault;

/// <summary>
/// Signs documents using a key stored in HashiCorp Vault or OpenBao's Transit secrets
/// engine. The private key never leaves Vault — we hash locally and submit the digest
/// for remote signing via the Transit <c>/sign</c> endpoint.
/// </summary>
/// <remarks>
/// Vault doesn't store the X.509 cert wrapper alongside the key. Adopters supply the
/// matching public cert via <see cref="VaultSealingOptions.CertificatePath"/>; it's loaded
/// from disk and embedded in the CMS SignedData.
/// </remarks>
public sealed class VaultSealingProvider : ICryptographicSealingProvider
{
    private readonly VaultSealingOptions _options;
    private readonly IVaultClient _vault;
    private readonly Lazy<X509Certificate2> _certificate;

    public VaultSealingProvider(VaultSealingOptions options, IVaultClient? vaultClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;

        _vault = vaultClient ?? new VaultClient(
            new VaultClientSettings(
                _options.VaultAddress!.ToString(),
                BuildAuthMethodInfo(_options)));

        _certificate = new Lazy<X509Certificate2>(LoadCertificate);
    }

    /// <summary>
    /// Constructs the VaultSharp <see cref="IAuthMethodInfo"/> for the configured
    /// authentication method. VaultSharp handles token caching and renewal internally
    /// for AppRole and Kubernetes — the client transparently re-logins when the lease
    /// expires, so no rotation code is needed here.
    /// </summary>
    private static IAuthMethodInfo BuildAuthMethodInfo(VaultSealingOptions options) => options.AuthMethod switch
    {
        VaultAuthMethod.Token => new TokenAuthMethodInfo(options.Token!),
        VaultAuthMethod.AppRole => new AppRoleAuthMethodInfo(
            mountPoint: options.AppRoleMountPath,
            roleId: options.AppRoleId!,
            secretId: options.AppRoleSecretId!),
        VaultAuthMethod.Kubernetes => new KubernetesAuthMethodInfo(
            mountPoint: options.KubernetesMountPath,
            roleName: options.KubernetesRole!,
            jwt: File.ReadAllText(options.KubernetesServiceAccountTokenPath)),
        _ => throw new InvalidOperationException(
            $"Unknown {nameof(VaultAuthMethod)} value: {options.AuthMethod}."),
    };

    /// <inheritdoc />
    public string Name => "Vault";

    /// <inheritdoc />
    public Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_certificate.Value);

    /// <inheritdoc />
    public async Task<byte[]> SignAsync(
        byte[] data,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Vault Transit /sign supports hash_algorithm=sha2-256/sha2-384/sha2-512 and
        // signature_algorithm=pkcs1v15 (or pss). We use pkcs1v15 to match the PAdES
        // signatures Adobe accepts.
        var request = new SignRequestOptions
        {
            Base64EncodedInput = Convert.ToBase64String(data),
            HashAlgorithm = MapHashAlgorithm(hashAlgorithm),
            SignatureAlgorithm = SignatureAlgorithm.pkcs1v15,
        };

        var response = await _vault.V1.Secrets.Transit
            .SignDataAsync(_options.KeyName!, request, mountPoint: _options.TransitMountPath)
            .ConfigureAwait(false);

        // Vault returns signatures as "vault:v1:base64sig". Strip the prefix.
        var raw = response.Data.Signature;
        var lastColon = raw.LastIndexOf(':');
        var base64 = lastColon >= 0 ? raw[(lastColon + 1)..] : raw;
        return Convert.FromBase64String(base64);
    }

    private X509Certificate2 LoadCertificate()
    {
        var path = _options.CertificatePath!;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Vault signing certificate not found at '{path}'. " +
                "Provide the public X.509 cert matching the Transit key via VaultSealingOptions.CertificatePath.",
                path);
        }

#pragma warning disable SYSLIB0057 // X509Certificate2 ctor — accept .pem, .cer, .crt by content sniffing.
        return new X509Certificate2(path);
#pragma warning restore SYSLIB0057
    }

    private static TransitHashAlgorithm MapHashAlgorithm(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256) return TransitHashAlgorithm.SHA2_256;
        if (algorithm == HashAlgorithmName.SHA384) return TransitHashAlgorithm.SHA2_384;
        if (algorithm == HashAlgorithmName.SHA512) return TransitHashAlgorithm.SHA2_512;
        throw new NotSupportedException($"Unsupported hash algorithm '{algorithm.Name}'.");
    }
}
