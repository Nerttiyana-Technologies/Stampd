using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Tsp;

using Stampd.Core.Sealing;

namespace Stampd.Timestamp.FreeTsa;

/// <summary>
/// An <see cref="ITimestampAuthorityProvider"/> that talks to an RFC 3161 HTTP timestamp
/// authority. Defaults to <see href="https://freetsa.org/tsr"/> — FreeTSA, a freely-usable
/// public TSA suitable for development, demos, and self-hosted production environments
/// where its terms of use are acceptable.
/// </summary>
/// <remarks>
/// For higher-volume production use, point this provider at a commercial TSA (DigiCert,
/// GlobalSign, Sectigo) or an internal one (Microsoft AD CS, OpenSSL ts).
/// </remarks>
public sealed class FreeTsaTimestampAuthorityProvider : ITimestampAuthorityProvider
{
    /// <summary>Default FreeTSA endpoint. Suitable for dev / demo / low-volume use.</summary>
    public static readonly Uri DefaultEndpoint = new("https://freetsa.org/tsr");

    private static readonly MediaTypeHeaderValue TimestampQueryContentType =
        MediaTypeHeaderValue.Parse("application/timestamp-query");

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public FreeTsaTimestampAuthorityProvider(HttpClient httpClient, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    /// <inheritdoc />
    public string Name => "FreeTSA";

    /// <inheritdoc />
    public async Task<byte[]> RequestTimestampAsync(
        byte[] dataToTimestamp,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataToTimestamp);

        var hash = ComputeHash(dataToTimestamp, hashAlgorithm);
        var tsaHashOid = MapToTsaHashOid(hashAlgorithm);

        var requestGen = new TimeStampRequestGenerator();
        requestGen.SetCertReq(true); // ask the TSA to include its cert chain in the response
        var tspRequest = requestGen.Generate(tsaHashOid, hash);
        var requestBytes = tspRequest.GetEncoded();

        using var requestContent = new ByteArrayContent(requestBytes);
        requestContent.Headers.ContentType = TimestampQueryContentType;

        using var httpResponse = await _httpClient
            .PostAsync(_endpoint, requestContent, cancellationToken)
            .ConfigureAwait(false);

        httpResponse.EnsureSuccessStatusCode();

        var responseBytes = await httpResponse.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var tspResponse = new TimeStampResponse(responseBytes);

        // Validate ties the response back to the request (nonce, hash match, etc.).
        tspResponse.Validate(tspRequest);

        if (tspResponse.Status != 0) // 0 == granted
        {
            var failInfo = tspResponse.GetFailInfo()?.IntValue.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"FreeTSA refused the timestamp request: status={tspResponse.Status.ToString(CultureInfo.InvariantCulture)}, " +
                $"failInfo={failInfo ?? "(none)"}, " +
                $"statusString={tspResponse.GetStatusString() ?? "(none)"}.");
        }

        return tspResponse.TimeStampToken.GetEncoded();
    }

    private static byte[] ComputeHash(byte[] data, HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return SHA256.HashData(data);
        }

        if (algorithm == HashAlgorithmName.SHA384)
        {
            return SHA384.HashData(data);
        }

        if (algorithm == HashAlgorithmName.SHA512)
        {
            return SHA512.HashData(data);
        }

        throw new NotSupportedException(
            $"FreeTsaTimestampAuthorityProvider does not support hash algorithm '{algorithm.Name}'.");
    }

    private static string MapToTsaHashOid(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return NistObjectIdentifiers.IdSha256.Id;
        }

        if (algorithm == HashAlgorithmName.SHA384)
        {
            return NistObjectIdentifiers.IdSha384.Id;
        }

        if (algorithm == HashAlgorithmName.SHA512)
        {
            return NistObjectIdentifiers.IdSha512.Id;
        }

        throw new NotSupportedException(
            $"FreeTsaTimestampAuthorityProvider does not support hash algorithm '{algorithm.Name}'.");
    }
}
