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
            // v1.3 #136 — propagate caller-supplied CreatedAtUtc when set so the
            // rate-limit window math agrees with what the OTP provider saw at issue
            // time. Falls back to UtcNow for pre-1.3 callers using the 4- or 5-arg
            // OtpChallenge constructor.
            CreatedAtUtc = challenge.CreatedAtUtcOrNow,
            FailedAttempts = challenge.FailedAttempts,
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
            entity.ExpiresAtUtc,
            FailedAttempts: entity.FailedAttempts,
            CreatedAtUtc: entity.CreatedAtUtc);
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

    /// <inheritdoc />
    public async Task<int> IncrementFailedAttemptsAsync(
        string verificationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

        // ExecuteUpdate translates to a single round-trip UPDATE ... SET FailedAttempts =
        // FailedAttempts + 1 — atomic against concurrent verify spam without an explicit
        // transaction. We read back the new count via a follow-up Find; doing it as one
        // statement would need a RETURNING clause, which EF Core 10 doesn't expose
        // portably across providers yet.
        var rowsAffected = await ctx.Set<OtpChallengeEntity>()
            .Where(c => c.VerificationId == verificationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            return -1;
        }

        var updated = await ctx.Set<OtpChallengeEntity>()
            .AsNoTracking()
            .Where(c => c.VerificationId == verificationId)
            .Select(c => (int?)c.FailedAttempts)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return updated ?? -1;
    }

    /// <inheritdoc />
    public async Task<int> CountInitiatesSinceAsync(
        string identifier,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

        // Two-phase scan because EF Core 10's SQLite provider can't translate
        // DateTimeOffset comparison to SQL (column is stored as TEXT, same v1.2 #115
        // issue Doc 16 named — but OtpChallengeEntity never got an epoch shadow
        // column, so the comparison happens client-side):
        //
        //   Phase 1: server-side translatable filter on Identifier (cheap, indexed
        //            implicitly by the few rows that ever match a single email).
        //   Phase 2: pull just the CreatedAtUtc values and count the in-window ones
        //            in .NET. The filtered set is bounded by recent-issuance rate;
        //            for the default 15-min window with the default 5-per-window cap
        //            this is at most a handful of values per Initiate call.
        //
        // Adding an epoch shadow column to OtpChallengeEntity would let this be one
        // server-side query — a v1.4 candidate when adopters' OTP volume justifies
        // the per-provider migration.
        var recentTimestamps = await ctx.Set<OtpChallengeEntity>()
            .AsNoTracking()
            .Where(c => c.Identifier == identifier)
            .Select(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return recentTimestamps.Count(t => t >= since);
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
