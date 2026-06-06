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

    /// <summary>
    /// v2.0 Slice D — JWT <c>sub</c> claim of the authenticated user who triggered this
    /// event, when applicable. Null for events that originate from the recipient flow
    /// (RecipientViewed/RecipientSigned/etc. — the recipient is identified via
    /// <see cref="RecipientId"/>, not this field), background workers, or any pre-v2
    /// audit row. Populated for admin operations (SigningRequestVoided, RecipientInvitationResent)
    /// and sender-side dispatches.
    /// </summary>
    public string? ActorUserId { get; set; }

    /// <summary>
    /// v2.0 Slice D — the role the actor was acting in at event time (most-privileged
    /// role held at the moment, per <c>ICurrentActorContext.ActiveRole</c>). Stored
    /// denormalized so audit consumers can filter "all admin actions" without joining
    /// to a separate user table. Same null semantics as <see cref="ActorUserId"/>.
    /// </summary>
    public string? ActorRole { get; set; }
}
