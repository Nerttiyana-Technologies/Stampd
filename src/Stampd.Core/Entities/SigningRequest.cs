namespace Stampd.Core.Entities;

/// <summary>
/// A dispatched signing workflow — a concrete instance of a <see cref="DocumentTemplate"/>
/// with named recipients, running through to completion, decline, or expiry.
/// </summary>
public sealed class SigningRequest
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentTemplateId { get; set; }
    public DocumentTemplate? DocumentTemplate { get; set; }

    /// <summary>Email subject line dispatched to recipients.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Optional message body included in invitation emails.</summary>
    public string? Message { get; set; }

    /// <summary>Identifier of the user who dispatched the request.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public SigningRequestStatus Status { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? DeclinedAtUtc { get; set; }
    public DateTimeOffset? VoidedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>Recorded reason when <see cref="Status"/> is <see cref="SigningRequestStatus.Declined"/> or <see cref="SigningRequestStatus.Voided"/>.</summary>
    public string? TerminationReason { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<Recipient> Recipients { get; set; } = new List<Recipient>();
    public ICollection<AuditEvent> AuditEvents { get; set; } = new List<AuditEvent>();

    /// <summary>Set once the workflow completes and the sealed document is persisted.</summary>
    public SignedDocumentRecord? SignedDocument { get; set; }
}
