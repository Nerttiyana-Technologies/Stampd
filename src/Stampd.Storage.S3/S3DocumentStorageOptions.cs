namespace Stampd.Storage.S3;

/// <summary>
/// Configuration for <see cref="S3DocumentStorageProvider"/>.
/// </summary>
public sealed class S3DocumentStorageOptions
{
    /// <summary>Target S3 bucket name. Required.</summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// AWS region, e.g. <c>us-east-1</c>. When null, the AWS SDK's default region
    /// resolution is used (env, profile, EC2 instance metadata).
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Optional prefix prepended to every stored object's key. Used for per-tenant
    /// segregation in shared buckets, e.g. <c>tenants/acme/</c>. Must end with a
    /// forward slash; if not, one is appended.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Optional KMS key ARN or alias for SSE-KMS encryption at rest. When null, S3 uses
    /// SSE-S3 (AES-256) with bucket-default keys, which still meets most compliance
    /// requirements. Set this to require a customer-managed key.
    /// </summary>
    public string? KmsKeyId { get; set; }

    /// <summary>
    /// Optional S3 endpoint override. Used for MinIO / LocalStack testing and for
    /// VPC-private S3 endpoints. When null, the SDK uses the regional default.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException(
                $"{nameof(S3DocumentStorageOptions)}.{nameof(BucketName)} is required.");
        }
    }
}
