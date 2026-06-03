using System.Globalization;
using System.Net;

using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

using Stampd.Core.Storage;

namespace Stampd.Storage.S3;

/// <summary>
/// Stores documents in an AWS S3 bucket. Keys follow the same <c>{shard}/{guid}/{name}</c>
/// shape used by the filesystem provider, so callers don't have to know which backend
/// they're talking to.
/// </summary>
/// <remarks>
/// Auth uses the AWS SDK's default credential chain — env vars, shared profile, EC2
/// instance metadata, ECS task role, EKS IRSA / Pod Identity. Encryption defaults to
/// SSE-S3 (bucket-default keys); set <see cref="S3DocumentStorageOptions.KmsKeyId"/> to
/// require SSE-KMS with a customer-managed key.
/// </remarks>
public sealed class S3DocumentStorageProvider : IDocumentStorageProvider, IDisposable
{
    private readonly S3DocumentStorageOptions _options;
    private readonly IAmazonS3 _s3;
    private readonly bool _ownsClient;
    private readonly string _keyPrefix;

    public S3DocumentStorageProvider(S3DocumentStorageOptions options, IAmazonS3? s3Client = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _keyPrefix = NormalizePrefix(options.KeyPrefix);

        if (s3Client is null)
        {
            var config = new AmazonS3Config();
            if (options.Region is not null)
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
            }
            if (options.ServiceUrl is not null)
            {
                config.ServiceURL = options.ServiceUrl.ToString();
                config.ForcePathStyle = true; // required for MinIO / LocalStack
            }
            _s3 = new AmazonS3Client(config);
            _ownsClient = true;
        }
        else
        {
            _s3 = s3Client;
            _ownsClient = false;
        }
    }

    /// <inheritdoc />
    public string Name => "S3";

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        ReadOnlyMemory<byte> bytes,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var shard = id.ToString("N", CultureInfo.InvariantCulture)[..2];
        var safeName = SanitizeForObjectKey(logicalName);
        var key = $"{_keyPrefix}{shard}/{id:N}/{safeName}";

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = "application/pdf",
        };

        if (!string.IsNullOrEmpty(_options.KmsKeyId))
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
            request.ServerSideEncryptionKeyManagementServiceKeyId = _options.KmsKeyId;
        }

        _ = await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task<byte[]> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        using var response = await _s3.GetObjectAsync(_options.BucketName, storageKey, cancellationToken)
            .ConfigureAwait(false);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        try
        {
            _ = await _s3.GetObjectMetadataAsync(_options.BucketName, storageKey, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return string.Empty;
        }

        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static string SanitizeForObjectKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "document";
        }

        // S3 keys allow most chars but we strip control characters and a few reserved
        // ones to keep CDN distributions and curl-friendliness happy.
        Span<char> output = stackalloc char[Math.Min(input.Length, 100)];
        var written = 0;
        for (var i = 0; i < input.Length && written < output.Length; i++)
        {
            var c = input[i];
            if (char.IsControl(c) || c is '\\' or ':' or '?' or '#' or '%' or '"' or '<' or '>' or '|')
            {
                output[written++] = '_';
            }
            else
            {
                output[written++] = c;
            }
        }

        return new string(output[..written]);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _s3.Dispose();
        }
    }
}
