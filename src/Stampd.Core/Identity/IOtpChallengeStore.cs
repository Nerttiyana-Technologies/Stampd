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
public sealed record OtpChallenge(
    string VerificationId,
    string Identifier,
    string Code,
    DateTimeOffset ExpiresAtUtc);

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
}
