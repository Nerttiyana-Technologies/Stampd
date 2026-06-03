using System.Globalization;

using Google;
using Google.Cloud.Storage.V1;

using Stampd.Core.Storage;

namespace Stampd.Storage.Gcs;

/// <summary>
/// Stores documents in a Google Cloud Storage bucket. Auth uses Application Default
/// Credentials — workload identity on GKE / Cloud Run, service-account JSON file,
/// gcloud user credentials for dev.
/// </summary>
public sealed class GcsDocumentStorageProvider : IDocumentStorageProvider, IDisposable
{
    private readonly GcsDocumentStorageOptions _options;
    private readonly StorageClient _client;
    private readonly string _keyPrefix;
    private readonly bool _ownsClient;

    public GcsDocumentStorageProvider(GcsDocumentStorageOptions options, StorageClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _keyPrefix = NormalizePrefix(options.KeyPrefix);

        if (client is null)
        {
            _client = StorageClient.Create();
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }
    }

    /// <inheritdoc />
    public string Name => "GCS";

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        ReadOnlyMemory<byte> bytes,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var shard = id.ToString("N", CultureInfo.InvariantCulture)[..2];
        var safeName = SanitizeForObjectName(logicalName);
        var key = $"{_keyPrefix}{shard}/{id:N}/{safeName}";

        var uploadOptions = new UploadObjectOptions();
        if (!string.IsNullOrEmpty(_options.KmsKeyName))
        {
            uploadOptions.KmsKeyName = _options.KmsKeyName;
        }

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        _ = await _client.UploadObjectAsync(
            bucket: _options.BucketName,
            objectName: key,
            contentType: "application/pdf",
            source: stream,
            options: uploadOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return key;
    }

    /// <inheritdoc />
    public async Task<byte[]> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        using var ms = new MemoryStream();
        _ = await _client.DownloadObjectAsync(
            bucket: _options.BucketName,
            objectName: storageKey,
            destination: ms,
            options: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        try
        {
            _ = await _client.GetObjectAsync(
                bucket: _options.BucketName,
                objectName: storageKey,
                options: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
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

    private static string SanitizeForObjectName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "document";
        }

        // GCS object names allow most chars; we keep the same conservative set used by
        // the S3 and Azure Blob providers so the key shape is identical across backends.
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
            _client.Dispose();
        }
    }
}
