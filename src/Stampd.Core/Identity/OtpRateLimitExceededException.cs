namespace Stampd.Core.Identity;

/// <summary>
/// Thrown by OTP <see cref="IIdentityVerificationProvider.InitiateAsync"/> implementations
/// when the caller's recent initiate rate exceeds the configured per-identifier window
/// (v1.3 #136). The WebApi catches this and returns HTTP 429 with a Retry-After header
/// so the recipient sees a clear "too many requests, try again in N minutes" surface.
/// </summary>
/// <remarks>
/// Separate from <see cref="IdentityVerificationResult"/> (which models per-verify failure)
/// because rate-limit rejection is a different failure mode: the request never made it to
/// the actual challenge generation, no code was sent, no email was burned, and the caller
/// should back off rather than retry immediately.
/// </remarks>
public sealed class OtpRateLimitExceededException : Exception
{
    /// <summary>
    /// How long the caller should wait before retrying. Maps directly to the HTTP
    /// <c>Retry-After</c> response header.
    /// </summary>
    public TimeSpan RetryAfter { get; }

    public OtpRateLimitExceededException(string identifier, TimeSpan retryAfter)
        : base($"Too many OTP requests for '{identifier}'. Retry after {retryAfter}.")
    {
        RetryAfter = retryAfter;
    }
}
