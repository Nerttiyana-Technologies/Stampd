namespace Stampd.Infrastructure.Identity;

/// <summary>
/// Persistent row backing the OTP challenge store. Code is hashed at rest with a per-row
/// salt so a compromised DB snapshot doesn't reveal in-flight codes.
/// </summary>
public sealed class OtpChallengeEntity
{
    /// <summary>Opaque verification id handed to the signer.</summary>
    public required string VerificationId { get; set; }

    /// <summary>Signer identifier — email address, phone number, etc.</summary>
    public required string Identifier { get; set; }

    /// <summary>SHA-256 hash of (code || salt) as a hex string.</summary>
    public required string CodeHash { get; set; }

    /// <summary>Per-row random salt (hex string).</summary>
    public required string Salt { get; set; }

    /// <summary>When the challenge becomes invalid.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>When the row was inserted. Used by cleanup background jobs.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Count of failed verify attempts. Reserved for v1.2 throttling — not enforced here
    /// to keep the store interface minimal.
    /// </summary>
    public int FailedAttempts { get; set; }
}
