using System.Globalization;

using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Stampd.Core.Storage;

namespace Stampd.Storage.AzureBlob;

/// <summary>
/// Stores documents in an Azure Storage Blob container. Auth uses a
/// <see cref="TokenCredential"/> — typically <c>DefaultAzureCredential</c> which chains
/// Managed Identity, workload identity, Azure CLI, environment vars.
/// </summary>
public sealed class AzureBlobDocumentStorageProvider : IDocumentStorageProvider
{
    private readonly AzureBlobDocumentStorageOptions _options;
    private readonly BlobContainerClient _container;
    private readonly string _keyPrefix;

    public AzureBlobDocumentStorageProvider(
        AzureBlobDocumentStorageOptions options,
        TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        options.Validate();

        _options = options;
        _keyPrefix = NormalizePrefix(options.KeyPrefix);

        // Encryption scope is configured at the client level (BlobClientOptions.EncryptionScope),
        // not per-upload — every blob written through this client picks up the scope, which
        // is the right semantics for a per-tenant storage backend anyway.
        var clientOptions = new BlobClientOptions();
        if (!string.IsNullOrEmpty(options.EncryptionScope))
        {
            clientOptions.EncryptionScope = options.EncryptionScope;
        }

        var serviceClient = new BlobServiceClient(options.AccountUri, credential, clientOptions);
        _container = serviceClient.GetBlobContainerClient(options.ContainerName);
    }

    /// <inheritdoc />
    public string Name => "AzureBlob";

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        ReadOnlyMemory<byte> bytes,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var shard = id.ToString("N", CultureInfo.InvariantCulture)[..2];
        var safeName = SanitizeForBlobName(logicalName);
        var key = $"{_keyPrefix}{shard}/{id:N}/{safeName}";

        var blob = _container.GetBlobClient(key);
        var uploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" },
        };

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        _ = await blob.UploadAsync(stream, uploadOptions, cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task<byte[]> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var blob = _container.GetBlobClient(storageKey);
        var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return response.Value.Content.ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        try
        {
            var blob = _container.GetBlobClient(storageKey);
            var response = await blob.ExistsAsync(cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
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

    private static string SanitizeForBlobName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "document";
        }

        // Azure blob names are essentially unrestricted in characters but limited to 1024
        // bytes UTF-8. We keep it tight for portability with other backends.
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
}
