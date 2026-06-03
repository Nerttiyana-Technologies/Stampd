using Stampd.Core.Storage;

namespace Stampd.Storage.FileSystem;

/// <summary>
/// Writes documents under a configured root directory, sharded by the first 2 chars of a
/// generated Guid so directories don't blow past filesystem-friendly entry counts.
/// </summary>
public sealed class FileSystemDocumentStorageProvider : IDocumentStorageProvider
{
    private readonly string _rootDirectory;

    public FileSystemDocumentStorageProvider(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <inheritdoc />
    public string Name => "FileSystem";

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        ReadOnlyMemory<byte> bytes,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var shard = id.ToString("N", System.Globalization.CultureInfo.InvariantCulture)[..2];
        var safeName = SanitizeForFilesystem(logicalName);
        var key = $"{shard}/{id:N}/{safeName}";

        var fullPath = Path.Combine(_rootDirectory, key);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await File.WriteAllBytesAsync(fullPath, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <inheritdoc />
    public async Task<byte[]> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        var fullPath = ResolveAndValidate(storageKey);
        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        var fullPath = ResolveAndValidate(storageKey);
        return Task.FromResult(File.Exists(fullPath));
    }

    private string ResolveAndValidate(string storageKey)
    {
        // Defend against path traversal: ensure the resolved path stays under _rootDirectory.
        var combined = Path.GetFullPath(Path.Combine(_rootDirectory, storageKey));
        var rootFull = Path.GetFullPath(_rootDirectory);
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, rootFull, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Storage key '{storageKey}' resolves outside the configured root.");
        }

        return combined;
    }

    private static string SanitizeForFilesystem(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "document";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var span = input.AsSpan();
        Span<char> output = stackalloc char[Math.Min(span.Length, 100)];
        var written = 0;
        for (var i = 0; i < span.Length && written < output.Length; i++)
        {
            var c = span[i];
            if (Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':')
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
