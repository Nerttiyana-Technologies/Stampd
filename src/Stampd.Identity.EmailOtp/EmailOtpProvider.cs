using System.Collections.Concurrent;
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
            .StoreAsync(new OtpChallenge(verificationId, subject.Email, code, expiresAt), cancellationToken)
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

        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(challenge.Code),
            System.Text.Encoding.UTF8.GetBytes(response.Trim())))
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

public sealed record OtpChallenge(string VerificationId, string Email, string Code, DateTimeOffset ExpiresAtUtc);

/// <summary>Pluggable challenge store. Default impl is in-memory; production swaps for DB/Redis.</summary>
public interface IOtpChallengeStore
{
    Task StoreAsync(OtpChallenge challenge, CancellationToken cancellationToken = default);
    Task<OtpChallenge?> RetrieveAsync(string verificationId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string verificationId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryOtpChallengeStore : IOtpChallengeStore
{
    private readonly ConcurrentDictionary<string, OtpChallenge> _store = new();

    public Task StoreAsync(OtpChallenge challenge, CancellationToken cancellationToken = default)
    {
        _store[challenge.VerificationId] = challenge;
        return Task.CompletedTask;
    }

    public Task<OtpChallenge?> RetrieveAsync(string verificationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryGetValue(verificationId, out var c) ? c : null);

    public Task RemoveAsync(string verificationId, CancellationToken cancellationToken = default)
    {
        _store.TryRemove(verificationId, out _);
        return Task.CompletedTask;
    }
}
