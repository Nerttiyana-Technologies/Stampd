namespace Stampd.Crypto.Vault;

/// <summary>
/// Configuration for <see cref="VaultSealingProvider"/>.
/// </summary>
public sealed class VaultSealingOptions
{
    /// <summary>
    /// Vault HTTP endpoint, e.g. <c>https://vault.example.com:8200</c>. Works for both
    /// HashiCorp Vault and OpenBao.
    /// </summary>
    public Uri? VaultAddress { get; set; }

    /// <summary>
    /// Vault auth token. For production use, prefer rotating tokens via AppRole, JWT, or
    /// Kubernetes auth — wire those through the auth method factory on the registration
    /// extension.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Mount path of the Transit secrets engine. Defaults to "transit".
    /// </summary>
    public string TransitMountPath { get; set; } = "transit";

    /// <summary>Name of the Transit key used for signing.</summary>
    public string? KeyName { get; set; }

    /// <summary>
    /// Local filesystem path to the X.509 certificate (.cer / .crt / .pem) whose public
    /// key matches the Transit key. Vault doesn't store the X.509 wrapper alongside the
    /// key — adopters provide it separately.
    /// </summary>
    public string? CertificatePath { get; set; }

    public void Validate()
    {
        if (VaultAddress is null)
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(VaultAddress)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(Token)} is required for this skeleton. " +
                "Production deployments should use a rotating auth method (AppRole, JWT, K8s) " +
                "by overriding the auth method factory on the DI registration extension.");
        }

        if (string.IsNullOrWhiteSpace(KeyName))
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(KeyName)} is required.");
        }

        if (string.IsNullOrWhiteSpace(CertificatePath))
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(CertificatePath)} is required " +
                "(the X.509 cert whose public key corresponds to the Transit key).");
        }
    }
}
