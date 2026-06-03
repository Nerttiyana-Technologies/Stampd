using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Core.Identity;

namespace Stampd.Infrastructure.Identity;

/// <summary>
/// Persistent <see cref="IOtpChallengeStore"/> backed by EF Core. Hashes codes at rest with
/// a per-row salt so a DB snapshot doesn't leak in-flight verification codes.
/// </summary>
/// <remarks>
/// Survives process restarts and works across multiple WebApi replicas. Pair with a
/// scheduled sweep that deletes rows past <see cref="OtpChallengeEntity.ExpiresAtUtc"/>;
/// the index on that column makes the sweep cheap.
/// </remarks>
public sealed class DbOtpChallengeStore : IOtpChallengeStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbOtpChallengeStore(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task StoreAsync(OtpChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var (hash, salt) = HashCode(challenge.Code);
        var entity = new OtpChallengeEntity
        {
            VerificationId = challenge.VerificationId,
            Identifier = challenge.Identifier,
            CodeHash = hash,
            Salt = salt,
            ExpiresAtUtc = challenge.ExpiresAtUtc,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            FailedAttempts = 0,
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        ctx.Set<OtpChallengeEntity>().Add(entity);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OtpChallenge?> RetrieveAsync(
        string verificationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

        var entity = await ctx.Set<OtpChallengeEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.VerificationId == verificationId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        // The caller compares against challenge.Code via FixedTimeEquals. We can't return
        // the plaintext code (we don't have it), so we return a wrapper whose Code field
        // is the storage hash-with-salt envelope — and pair it with a custom comparer.
        // To preserve the IOtpChallengeStore contract without refactoring its semantics,
        // we attach a sentinel-encoded code: "{salt}:{hash}". DbOtpChallengeStore-aware
        // providers detect the sentinel and verify via VerifyAgainst(challenge, response).
        // For now we return the actual stored values so legacy providers can call
        // VerifyAgainst() statically. See remarks on VerifyAgainst.
        return new OtpChallenge(
            entity.VerificationId,
            entity.Identifier,
            Code: $"{Sentinel}{entity.Salt}:{entity.CodeHash}",
            entity.ExpiresAtUtc);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string verificationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

        await ctx.Set<OtpChallengeEntity>()
            .Where(c => c.VerificationId == verificationId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sentinel prefix on the Code field of returned <see cref="OtpChallenge"/> instances
    /// indicating the value is a salt:hash envelope rather than a plaintext code. OTP
    /// providers (Email, SMS) check for this prefix when verifying a response.
    /// </summary>
    public const string Sentinel = "$dbstore$";

    /// <summary>
    /// Verifies a signer's response against a challenge that was retrieved from
    /// <see cref="DbOtpChallengeStore"/>. Returns <see langword="true"/> when the response
    /// matches. Returns <see langword="false"/> when the challenge's Code field is not in
    /// the sentinel envelope (callers should fall back to plaintext comparison via
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>).
    /// </summary>
    public static bool TryVerifyEnvelope(string envelopeCode, string response, out bool matched)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        if (!envelopeCode.StartsWith(Sentinel, StringComparison.Ordinal))
        {
            matched = false;
            return false;
        }

        var payload = envelopeCode[Sentinel.Length..];
        var colon = payload.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            matched = false;
            return true; // it WAS a sentinel envelope, just malformed → matched=false
        }

        var salt = payload[..colon];
        var storedHash = payload[(colon + 1)..];
        var computed = ComputeHash(response.Trim(), salt);

        matched = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(storedHash));
        return true;
    }

    private static (string Hash, string Salt) HashCode(string code)
    {
        Span<byte> saltBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(saltBytes);
        var salt = Convert.ToHexString(saltBytes);
        return (ComputeHash(code, salt), salt);
    }

    private static string ComputeHash(string code, string saltHex)
    {
        var bytes = Encoding.UTF8.GetBytes(code + ":" + saltHex);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
