namespace Stampd.Crypto.AwsKms;

/// <summary>
/// Configuration for <see cref="AwsKmsSealingProvider"/>.
/// </summary>
public sealed class AwsKmsSealingOptions
{
    /// <summary>
    /// KMS key identifier — can be a key ID (<c>1234abcd-...</c>), an alias
    /// (<c>alias/document-signing</c>), or a full ARN. ARN is preferred for clarity.
    /// The key must be an asymmetric RSA signing key (RSA_2048, RSA_3072, or RSA_4096)
    /// with KeyUsage = SIGN_VERIFY.
    /// </summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// AWS region for the KMS client, e.g. <c>us-east-1</c>. When null, the AWS SDK's
    /// default resolution chain is used (env vars, profile, EC2 metadata).
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Local filesystem path to the X.509 certificate (.cer / .crt / .pem) whose public
    /// key matches the KMS signing key. KMS doesn't store the X.509 wrapper — adopters
    /// provide it separately (typically issued by a CA after submitting a CSR generated
    /// from the KMS public key).
    /// </summary>
    public string? CertificatePath { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyId))
        {
            throw new InvalidOperationException(
                $"{nameof(AwsKmsSealingOptions)}.{nameof(KeyId)} is required " +
                "(KMS key ID, alias, or ARN).");
        }

        if (string.IsNullOrWhiteSpace(CertificatePath))
        {
            throw new InvalidOperationException(
                $"{nameof(AwsKmsSealingOptions)}.{nameof(CertificatePath)} is required " +
                "(the X.509 cert whose public key corresponds to the KMS signing key).");
        }
    }
}
