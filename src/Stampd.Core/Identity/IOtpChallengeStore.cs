using System.Collections.Concurrent;

namespace Stampd.Core.Identity;

/// <summary>
/// A persistent challenge issued by an OTP-based identity verification provider.
/// </summary>
/// <param name="VerificationId">
/// Opaque identifier handed back to the signer. Used to correlate the response with the
/// challenge.
/// </param>
/// <param name="Identifier">
/// The signer's identifier the challenge was sent to — email address for
/// <c>EmailOtpProvider</c>, phone number for <c>SmsOtpProvider</c>. Generic name keeps this
/// interface reusable across channels.
/// </param>
/// <param name="Code">
/// The challenge value the signer must echo back. In-memory implementations may store this
/// in plaintext; persistent stores SHOULD hash it at rest.
/// and equivalent persistent implementations do exactly that.
/// </param>
/// <param name="ExpiresAtUtc">When the challenge becomes invalid.</param>
/// <param name="FailedAttempts">
/// Count of incorrect verification attempts against this specific challenge. v1.3 #136
/// uses this for brute-force lockout — when the count hits a per-provider threshold the
/// challenge is removed and the recipient must request a new code. Defaults to 0 so
/// pre-1.3 callers constructing OtpChallenge with the 4-arg form keep working.
/// </param>
/// <param name="CreatedAtUtc">
/// When the challenge was issued. v1.3 #136 — the initiate-rate-limit window is
/// measured against this timestamp. Defaults to <c>DateTimeOffset.UtcNow</c> via a
/// factory default so pre-1.3 callers constructing OtpChallenge with the 4- or 5-arg
/// form get a sensible value automatically.
/// </param>
public sealed record OtpChallenge(
    string VerificationId,
    string Identifier,
    string Code,
    DateTimeOffset ExpiresAtUtc,
    int FailedAttempts = 0,
    DateTimeOffset? CreatedAtUtc = null)
{
    /// <summary>
    /// Non-null variant of <see cref="CreatedAtUtc"/> — defaults to the moment the record
    /// was constructed when the caller didn't supply a value, so the rate-limit window
    /// math is always against a real timestamp.
    /// </summary>
    public DateTimeOffset CreatedAtUtcOrNow => CreatedAtUtc ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// Pluggable challenge store backing the OTP identity verification providers (email, SMS).
/// Default in-process implementation is in-memory; production multi-node deployments should
/// swap in a persistent store (DB, Redis).
/// </summary>
public interface IOtpChallengeStore
{
    Task StoreAsync(OtpChallenge challenge, CancellationToken cancellationToken = default);
    Task<OtpChallenge?> RetrieveAsync(string verificationId, CancellationToken cancellationToken = default);
    Task RemoveAsync(string verificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically bumps <c>FailedAttempts</c> on the named challenge and returns the new
    /// value. Returns -1 when the challenge no longer exists (already removed by
    /// expiry sweep, prior lockout, or successful verify). v1.3 #136 — brute-force
    /// lockout primitive used by <c>EmailOtpProvider.VerifyAsync</c> /
    /// <c>SmsOtpProvider.VerifyAsync</c>.
    /// </summary>
    Task<int> IncrementFailedAttemptsAsync(string verificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of challenges issued for <paramref name="identifier"/> at or
    /// after <paramref name="since"/>. v1.3 #136 — initiate-rate-limit primitive used
    /// by the providers' InitiateAsync to detect abuse before generating a new code.
    /// "Initiated" means the challenge exists in the store regardless of verify state;
    /// successful verifies remove the challenge so they no longer count toward the
    /// caller's window.
    /// </summary>
    Task<int> CountInitiatesSinceAsync(
        string identifier,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-process in-memory store. Loses state on restart and doesn't survive multi-node
/// deployments. Adequate for single-node dev and tests; for production wire a persistent
/// implementation.
/// </summary>
public sealed class InMemoryOtpChallengeStore : IOtpChallengeStore
{
    private readonly ConcurrentDictionary<string, OtpChallenge> _store = new();

    public Task StoreAsync(OtpChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
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

    /// <inheritdoc />
    public Task<int> IncrementFailedAttemptsAsync(string verificationId, CancellationToken cancellationToken = default)
    {
        // ConcurrentDictionary.AddOrUpdate is read-modify-write atomic via internal locks
        // — safe for the multi-tab/multi-request races OTP verify-spam would create.
        if (!_store.TryGetValue(verificationId, out var existing))
        {
            return Task.FromResult(-1);
        }
        var updated = existing with { FailedAttempts = existing.FailedAttempts + 1 };
        _store[verificationId] = updated;
        return Task.FromResult(updated.FailedAttempts);
    }

    /// <inheritdoc />
    public Task<int> CountInitiatesSinceAsync(
        string identifier,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var c in _store.Values)
        {
            if (string.Equals(c.Identifier, identifier, StringComparison.OrdinalIgnoreCase)
                && c.CreatedAtUtcOrNow >= since)
            {
                count++;
            }
        }
        return Task.FromResult(count);
    }
}
