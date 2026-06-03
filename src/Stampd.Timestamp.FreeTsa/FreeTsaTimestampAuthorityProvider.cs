using System.Security.Cryptography;

using Stampd.Core.Sealing;
using Stampd.Timestamp.Rfc3161;

namespace Stampd.Timestamp.FreeTsa;

/// <summary>
/// FreeTSA preset over the generic <see cref="Rfc3161TimestampAuthorityProvider"/>.
/// Defaults to <see href="https://freetsa.org/tsr"/>.
/// </summary>
/// <remarks>
/// Kept for backward compatibility with code (sample CLIs, third-party adopters) that
/// constructed <c>FreeTsaTimestampAuthorityProvider</c> directly. For new code prefer
/// <see cref="Rfc3161TimestampAuthorityProvider"/> with explicit options — it works against
/// any RFC 3161 TSA including FreeTSA.
/// </remarks>
public sealed class FreeTsaTimestampAuthorityProvider : ITimestampAuthorityProvider
{
    /// <summary>Default FreeTSA endpoint. Suitable for dev / demo / low-volume use.</summary>
    public static readonly Uri DefaultEndpoint = new("https://freetsa.org/tsr");

    private readonly Rfc3161TimestampAuthorityProvider _inner;

    public FreeTsaTimestampAuthorityProvider(HttpClient httpClient, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _inner = new Rfc3161TimestampAuthorityProvider(httpClient, new Rfc3161TimestampAuthorityOptions
        {
            Name = "FreeTSA",
            Endpoint = endpoint ?? DefaultEndpoint,
            RequestTsaCertificate = true,
            IncludeNonce = true,
        });
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task<byte[]> RequestTimestampAsync(
        byte[] dataToTimestamp,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
        => _inner.RequestTimestampAsync(dataToTimestamp, hashAlgorithm, cancellationToken);
}
