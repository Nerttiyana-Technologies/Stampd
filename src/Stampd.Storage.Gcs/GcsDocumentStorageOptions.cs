namespace Stampd.Storage.Gcs;

/// <summary>
/// Configuration for <see cref="GcsDocumentStorageProvider"/>.
/// </summary>
public sealed class GcsDocumentStorageOptions
{
    /// <summary>Target GCS bucket name. Required.</summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// Optional prefix prepended to every stored object's name. Used for per-tenant
    /// segregation in shared buckets.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Optional Cloud KMS key resource name for CMEK encryption,
    /// e.g. <c>projects/p/locations/l/keyRings/r/cryptoKeys/k</c>. When null, GCS uses
    /// Google-managed encryption keys.
    /// </summary>
    public string? KmsKeyName { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException(
                $"{nameof(GcsDocumentStorageOptions)}.{nameof(BucketName)} is required.");
        }
    }
}
