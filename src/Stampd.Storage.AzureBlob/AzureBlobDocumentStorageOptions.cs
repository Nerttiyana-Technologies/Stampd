namespace Stampd.Storage.AzureBlob;

/// <summary>
/// Configuration for <see cref="AzureBlobDocumentStorageProvider"/>.
/// </summary>
public sealed class AzureBlobDocumentStorageOptions
{
    /// <summary>
    /// URI of the storage account, e.g. <c>https://acme.blob.core.windows.net</c>.
    /// Required.
    /// </summary>
    public Uri? AccountUri { get; set; }

    /// <summary>Target container name. Required.</summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// Optional prefix prepended to every stored blob's name. Used for per-tenant
    /// segregation in shared containers, e.g. <c>tenants/acme/</c>.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Optional name of a customer-managed key (in Azure Key Vault) used for encryption.
    /// Container-level encryption scopes are configured via Azure; this option just
    /// selects which scope to apply per blob.
    /// </summary>
    public string? EncryptionScope { get; set; }

    public void Validate()
    {
        if (AccountUri is null)
        {
            throw new InvalidOperationException(
                $"{nameof(AzureBlobDocumentStorageOptions)}.{nameof(AccountUri)} is required.");
        }

        if (string.IsNullOrWhiteSpace(ContainerName))
        {
            throw new InvalidOperationException(
                $"{nameof(AzureBlobDocumentStorageOptions)}.{nameof(ContainerName)} is required.");
        }
    }
}
