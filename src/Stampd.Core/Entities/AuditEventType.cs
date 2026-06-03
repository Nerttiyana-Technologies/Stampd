namespace Stampd.Core.Entities;

/// <summary>
/// The catalog of events captured into the append-only audit trail.
/// </summary>
/// <remarks>
/// New event types should always be added at the end; underlying integer values are
/// persisted and must remain stable. Existing values must never be reassigned.
/// </remarks>
public enum AuditEventType
{
    TemplateCreated = 0,
    TemplateUpdated = 1,
    TemplateArchived = 2,

    SigningRequestCreated = 100,
    SigningRequestSent = 101,
    SigningRequestVoided = 102,
    SigningRequestExpired = 103,
    SigningRequestCompleted = 104,

    RecipientInvited = 200,
    RecipientViewed = 201,
    RecipientConsentCaptured = 202,
    RecipientIdentityVerified = 203,
    RecipientFieldFilled = 204,
    RecipientSigned = 205,
    RecipientDeclined = 206,
    RecipientExpired = 207,

    DocumentSealed = 300,
    DocumentDownloaded = 301,
    DocumentRedacted = 302,
}
