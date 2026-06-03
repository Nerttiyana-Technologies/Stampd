namespace Stampd.Core.Entities;

/// <summary>
/// One row in the append-only audit trail. Audit rows are never updated or deleted; GDPR
/// erasure redacts personal fields in-place while preserving event provenance.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SigningRequestId { get; set; }
    public SigningRequest? SigningRequest { get; set; }

    public Guid? RecipientId { get; set; }
    public Recipient? Recipient { get; set; }

    public AuditEventType EventType { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    // ----- Forensic context -----
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? GeoCountry { get; set; }
    public string? GeoCity { get; set; }

    /// <summary>SHA-256 of the document at the moment this event occurred. Used to prove the document was unchanged between events.</summary>
    public string? DocumentHashAtEvent { get; set; }

    /// <summary>JSON-encoded event-specific payload. Provider-agnostic <c>string</c> column; the Postgres provider configures this as <c>jsonb</c>.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>True after a GDPR erasure has redacted personal fields. Provenance (Id, EventType, OccurredAtUtc) remains intact.</summary>
    public bool IsRedacted { get; set; }
}
