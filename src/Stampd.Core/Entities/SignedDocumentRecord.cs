namespace Stampd.Core.Entities;

/// <summary>
/// The terminal artifact of a successful workflow: a sealed PDF byte stream referenced by
/// storage key, plus the cryptographic provenance needed to re-verify it later.
/// </summary>
public sealed class SignedDocumentRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SigningRequestId { get; set; }
    public SigningRequest? SigningRequest { get; set; }

    /// <summary>Opaque key into the configured <c>IDocumentStorageProvider</c>.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of the sealed PDF bytes, hex-encoded lowercase.</summary>
    public string ContentSha256 { get; set; } = string.Empty;

    public DateTimeOffset SignedAtUtc { get; set; }

    /// <summary>
    /// Unix epoch milliseconds copy of <see cref="SignedAtUtc"/>. Lets the recipient
    /// signed-document download endpoint pick the newest record for a given signing
    /// request via server-side ORDER BY on SQLite. Stamped on insert; immutable
    /// thereafter (SignedDocumentRecord rows are append-only).
    /// </summary>
    public long SignedAtUtcEpochMs { get; set; }

    /// <summary>The <c>ICryptographicSealingProvider</c> that produced the signature ("AzureKeyVault", "Vault", "LocalCertificate").</summary>
    public string SealingProviderName { get; set; } = string.Empty;

    /// <summary>Provider-specific key identifier (Key Vault URI, Vault key path, certificate thumbprint).</summary>
    public string SealingKeyIdentifier { get; set; } = string.Empty;

    /// <summary>RFC 3161 timestamp authority URL if a trusted timestamp was embedded. Null for PAdES B-B.</summary>
    public string? TimestampAuthorityUrl { get; set; }

    /// <summary>Achieved PAdES conformance level.</summary>
    public PAdESLevel PAdESLevel { get; set; }
}
