namespace Stampd.Crypto.AzureKeyVault;

/// <summary>
/// Configuration for <see cref="AzureKeyVaultSealingProvider"/>.
/// </summary>
public sealed class AzureKeyVaultSealingOptions
{
    /// <summary>
    /// Full key identifier (versioned URI) for the signing key, e.g.
    /// <c>https://my-vault.vault.azure.net/keys/document-signing/abc123def...</c>.
    /// </summary>
    public Uri? KeyIdentifier { get; set; }

    /// <summary>
    /// URI of the Key Vault holding the matching X.509 certificate. Often the same vault
    /// as the key, but Stampd allows them to differ.
    /// </summary>
    public Uri? CertificateVaultUri { get; set; }

    /// <summary>Name (not version) of the certificate to download.</summary>
    public string? CertificateName { get; set; }

    public void Validate()
    {
        if (KeyIdentifier is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AzureKeyVaultSealingOptions)}.{nameof(KeyIdentifier)} is required.");
        }

        if (CertificateVaultUri is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AzureKeyVaultSealingOptions)}.{nameof(CertificateVaultUri)} is required.");
        }

        if (string.IsNullOrWhiteSpace(CertificateName))
        {
            throw new InvalidOperationException(
                $"{nameof(AzureKeyVaultSealingOptions)}.{nameof(CertificateName)} is required.");
        }
    }
}
