using System.Security.Cryptography;

using Stampd.Core.Identity;

namespace Stampd.Identity.SmsOtp;

/// <summary>
/// Issues a 6-digit OTP code via SMS, validates the response against the configured
/// <see cref="IOtpChallengeStore"/>. Mirrors the email-OTP provider in semantics — same
/// store contract, same fixed-time comparison, same sentinel-envelope handling for
/// persistent stores that hash codes at rest.
/// </summary>
/// <remarks>
/// <para>
/// Production hardening to-dos (shared with email-OTP):
/// </para>
/// <list type="bullet">
///   <item>Per-recipient rate limiting on InitiateAsync.</item>
///   <item>Throttling on VerifyAsync after N failed attempts.</item>
///   <item>HMAC the verification ID so it can't be guessed.</item>
/// </list>
/// </remarks>
public sealed class SmsOtpProvider : IIdentityVerificationProvider
{
    private readonly ISmsGateway _smsGateway;
    private readonly IOtpChallengeStore _store;
    private readonly SmsOtpOptions _options;

    public SmsOtpProvider(ISmsGateway smsGateway, IOtpChallengeStore store, SmsOtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(smsGateway);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _smsGateway = smsGateway;
        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "SmsOtp";

    /// <inheritdoc />
    public async Task<IdentityVerificationChallenge> InitiateAsync(
        IdentityVerificationSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (string.IsNullOrWhiteSpace(subject.PhoneNumber))
        {
            throw new InvalidOperationException(
                "SmsOtpProvider requires IdentityVerificationSubject.PhoneNumber to be non-empty.");
        }

        var code = GenerateCode();
        var verificationId = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(_options.ChallengeLifetime);

        await _store
            .StoreAsync(new OtpChallenge(verificationId, Identifier: subject.PhoneNumber, Code: code, ExpiresAtUtc: expiresAt), cancellationToken)
            .ConfigureAwait(false);

        var message = $"{_options.ProductName} verification code: {code}. " +
                      $"Expires in {_options.ChallengeLifetime.TotalMinutes:F0} minutes.";

        await _smsGateway
            .SendAsync(subject.PhoneNumber, message, cancellationToken)
            .ConfigureAwait(false);

        return new IdentityVerificationChallenge(
            verificationId,
            expiresAt,
            UserVisibleHint: $"We sent a 6-digit code to {MaskPhone(subject.PhoneNumber)}.");
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

    private static string GenerateCode()
    {
        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string MaskPhone(string phone)
    {
        if (phone.Length <= 4) return "***";
        return $"***{phone[^4..]}";
    }

    private static bool VerifySentinelEnvelope(string envelopeCode, string response)
    {
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
}

public sealed class SmsOtpOptions
{
    public string ProductName { get; set; } = "Stampd";
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);
}
