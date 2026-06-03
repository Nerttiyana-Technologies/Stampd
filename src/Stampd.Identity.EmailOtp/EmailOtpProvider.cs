using System.Security.Cryptography;

using Stampd.Core.Identity;
using Stampd.Core.Notifications;

namespace Stampd.Identity.EmailOtp;

/// <summary>
/// Issues a 6-digit OTP code by email, validates the response against an in-memory store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skeleton scope:</b> the challenge store is in-memory and per-instance. Restart the
/// process and unverified challenges are lost. Multi-node deployments need a shared store
/// (Redis, SQL Server) — wire by swapping <see cref="IOtpChallengeStore"/>.
/// </para>
/// <para>
/// <b>Production hardening to-dos</b> before this is ready for real users:
/// </para>
/// <list type="bullet">
///   <item>Persistent challenge store (DB) instead of ConcurrentDictionary.</item>
///   <item>Per-recipient rate limiting on InitiateAsync.</item>
///   <item>Throttling on VerifyAsync after N failed attempts.</item>
///   <item>HMAC the verification ID so it can't be guessed.</item>
///   <item>Hash the OTP at rest (don't store the raw code).</item>
/// </list>
/// </remarks>
public sealed class EmailOtpProvider : IIdentityVerificationProvider
{
    private readonly IEmailSender _emailSender;
    private readonly IOtpChallengeStore _store;
    private readonly EmailOtpOptions _options;

    public EmailOtpProvider(
        IEmailSender emailSender,
        IOtpChallengeStore store,
        EmailOtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(emailSender);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _emailSender = emailSender;
        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "EmailOtp";

    /// <inheritdoc />
    public async Task<IdentityVerificationChallenge> InitiateAsync(
        IdentityVerificationSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var code = GenerateCode();
        var verificationId = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(_options.ChallengeLifetime);

        await _store
            .StoreAsync(new OtpChallenge(verificationId, Identifier: subject.Email, Code: code, ExpiresAtUtc: expiresAt), cancellationToken)
            .ConfigureAwait(false);

        await _emailSender.SendAsync(new EmailMessage(
            FromAddress: _options.FromAddress,
            FromDisplayName: _options.FromDisplayName,
            To: [new EmailAddress(subject.Email, subject.DisplayName)],
            Subject: $"{_options.ProductName} verification code: {code}",
            PlainTextBody: $"Your verification code is {code}. It expires in {_options.ChallengeLifetime.TotalMinutes:F0} minutes.",
            HtmlBody: $"<p>Your verification code is <strong>{code}</strong>.</p><p>It expires in {_options.ChallengeLifetime.TotalMinutes:F0} minutes.</p>"),
            cancellationToken).ConfigureAwait(false);

        return new IdentityVerificationChallenge(
            verificationId,
            expiresAt,
            UserVisibleHint: $"We sent a 6-digit code to {MaskEmail(subject.Email)}.");
    }

    /// <inheritdoc />
    public async Task<IdentityVerificationResult> VerifyAsync(
        string verificationId,
        string response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        var challenge = await _store.RetrieveAsync(verificationId, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Verification not found or expired.");
        }

        if (challenge.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            await _store.RemoveAsync(verificationId, cancellationToken).ConfigureAwait(false);
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Verification code expired.");
        }

        bool matched;
        if (challenge.Code.StartsWith("$dbstore$", StringComparison.Ordinal))
        {
            // Persistent store returned a salt:hash envelope — we never have access to the
            // plaintext code. Compute SHA-256(response||salt) and compare against the stored
            // hash. The envelope format and verification helper live in DbOtpChallengeStore;
            // we reproduce the comparison inline here to avoid taking a hard reference from
            // this OTP provider to the Infrastructure project.
            matched = VerifySentinelEnvelope(challenge.Code, response.Trim());
        }
        else
        {
            matched = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(challenge.Code),
                System.Text.Encoding.UTF8.GetBytes(response.Trim()));
        }

        if (!matched)
        {
            return new IdentityVerificationResult(Succeeded: false, FailureReason: "Incorrect code.");
        }

        await _store.RemoveAsync(verificationId, cancellationToken).ConfigureAwait(false);
        return new IdentityVerificationResult(Succeeded: true);
    }

    private static bool VerifySentinelEnvelope(string envelopeCode, string response)
    {
        // Envelope: "$dbstore${saltHex}:{hashHex}"
        const string sentinel = "$dbstore$";
        var payload = envelopeCode[sentinel.Length..];
        var colon = payload.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var saltHex = payload[..colon];
        var storedHash = payload[(colon + 1)..];

        var bytes = System.Text.Encoding.UTF8.GetBytes(response + ":" + saltHex);
        var computed = Convert.ToHexString(SHA256.HashData(bytes));

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computed),
            System.Text.Encoding.UTF8.GetBytes(storedHash));
    }

    private static string GenerateCode()
    {
        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 1) return "***";
        return $"{email[0]}***{email[at..]}";
    }
}

public sealed class EmailOtpOptions
{
    public string FromAddress { get; set; } = "noreply@stampd.local";
    public string? FromDisplayName { get; set; } = "Stampd";
    public string ProductName { get; set; } = "Stampd";
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

// OtpChallenge, IOtpChallengeStore, and InMemoryOtpChallengeStore live in
// Stampd.Core.Identity since the SmsOtp provider needs them too. This file used to host
// them; type-forward aliases below preserve the old namespace for any adopters who
// referenced them directly. Prefer the Stampd.Core.Identity types in new code.
