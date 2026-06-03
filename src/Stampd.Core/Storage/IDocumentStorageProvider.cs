namespace Stampd.Core.Storage;

/// <summary>
/// Persists and retrieves opaque document byte streams. Stampd never embeds raw PDF bytes
/// in the relational store — they're written here and referenced by an opaque storage key.
/// </summary>
/// <remarks>
/// Implementations: local filesystem (dev), Azure Blob, AWS S3, GCS, etc. The interface
/// stays narrow (store / retrieve) so adopters can supply their own implementation without
/// changing engine code.
/// </remarks>
public interface IDocumentStorageProvider
{
    /// <summary>Short identifying name recorded into the audit trail.</summary>
    string Name { get; }

    /// <summary>
    /// Stores <paramref name="bytes"/> and returns the opaque key used to retrieve it
    /// later. Implementations should treat <paramref name="logicalName"/> as a hint for
    /// human-readable naming; the returned key is the source of truth.
    /// </summary>
    Task<string> StoreAsync(
        ReadOnlyMemory<byte> bytes,
        string logicalName,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves the bytes previously stored under <paramref name="storageKey"/>.</summary>
    Task<byte[]> RetrieveAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>Returns true if <paramref name="storageKey"/> identifies an existing document.</summary>
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);
}
