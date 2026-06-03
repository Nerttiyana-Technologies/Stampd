namespace Stampd.Core.Identity;

/// <summary>
/// Verifies the identity of a signer before they're allowed to sign. Pluggable: email OTP,
/// SMS OTP, KBA (knowledge-based authentication), government ID upload, qualified eID, etc.
/// </summary>
/// <remarks>
/// Two-phase API: <c>InitiateAsync</c> sends the challenge (e.g. OTP code), returns a
/// verification id. <c>VerifyAsync</c> takes the verification id + the signer's response
/// (the code they typed) and reports success/failure.
/// </remarks>
public interface IIdentityVerificationProvider
{
    /// <summary>Short identifying name recorded into the audit trail and onto <c>Recipient.IdentityVerificationMethod</c>.</summary>
    string Name { get; }

    /// <summary>Issues a verification challenge (e.g. emails an OTP code).</summary>
    Task<IdentityVerificationChallenge> InitiateAsync(
        IdentityVerificationSubject subject,
        CancellationToken cancellationToken = default);

    /// <summary>Validates the signer's response to a previously-issued challenge.</summary>
    Task<IdentityVerificationResult> VerifyAsync(
        string verificationId,
        string response,
        CancellationToken cancellationToken = default);
}

public sealed record IdentityVerificationSubject(
    string Email,
    string DisplayName,
    string? PhoneNumber = null);

public sealed record IdentityVerificationChallenge(
    string VerificationId,
    DateTimeOffset ExpiresAtUtc,
    string? UserVisibleHint = null);

public sealed record IdentityVerificationResult(
    bool Succeeded,
    string? FailureReason = null);
