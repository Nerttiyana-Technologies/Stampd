namespace Stampd.Core.Entities;

/// <summary>
/// A reusable PDF + field layout that the sender configures once and dispatches many times.
/// Templates are the design-time artifact; <see cref="SigningRequest"/> instances are the
/// run-time artifact derived from them.
/// </summary>
public sealed class DocumentTemplate
{
    /// <summary>Stable identifier for the template.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns the template. Subject to global query filter.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Human-readable template name shown in the sender UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional internal description / notes for the sender's reference.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Opaque key into the configured <c>IDocumentStorageProvider</c> identifying where the
    /// source PDF lives. Stampd never persists raw PDF bytes in the relational store.
    /// </summary>
    public string SourcePdfStorageKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of the source PDF, captured at upload time for tamper detection.</summary>
    public string SourcePdfSha256 { get; set; } = string.Empty;

    /// <summary>Identifier of the user who created the template.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Soft-archive flag; archived templates remain queryable but cannot be sent.</summary>
    public bool IsArchived { get; set; }

    /// <summary>Optimistic concurrency token. Updated on every save by the DbContext.</summary>
    public Guid ConcurrencyToken { get; set; }

    public ICollection<TemplateRecipientRole> Roles { get; set; } = new List<TemplateRecipientRole>();
    public ICollection<TemplateField> Fields { get; set; } = new List<TemplateField>();
}
