namespace Stampd.Core.Entities;

/// <summary>
/// A named participant in a <see cref="SigningRequest"/>. One recipient per role per request.
/// </summary>
public sealed class Recipient
{
    public Guid Id { get; set; }

    public Guid SigningRequestId { get; set; }
    public SigningRequest? SigningRequest { get; set; }

    /// <summary>The template role this recipient fills. Snapshot at dispatch time.</summary>
    public Guid? RoleId { get; set; }
    public TemplateRecipientRole? Role { get; set; }

    /// <summary>1-based routing order copied from the role at dispatch time.</summary>
    public int RoutingOrder { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public RecipientStatus Status { get; set; }

    public DateTimeOffset? InvitedAtUtc { get; set; }
    public DateTimeOffset? FirstViewedAtUtc { get; set; }
    public DateTimeOffset? SignedAtUtc { get; set; }
    public DateTimeOffset? DeclinedAtUtc { get; set; }
    public DateTimeOffset? IdentityVerifiedAtUtc { get; set; }

    /// <summary>Method used to verify identity (e.g. "EmailOtp", "Sms", "Kba", "IdDocument"). Set by the <c>IIdentityVerificationProvider</c>.</summary>
    public string? IdentityVerificationMethod { get; set; }

    public string? DeclineReason { get; set; }

    /// <summary>Opaque per-recipient signing link token. Single-use; rotated on each invite resend.</summary>
    public string AccessToken { get; set; } = string.Empty;
}
