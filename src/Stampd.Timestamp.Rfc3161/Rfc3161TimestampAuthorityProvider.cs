using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Tsp;

using Stampd.Core.Sealing;

namespace Stampd.Timestamp.Rfc3161;

/// <summary>
/// A generic, configurable <see cref="ITimestampAuthorityProvider"/> that speaks RFC 3161
/// over HTTP. Works with any compliant TSA — FreeTSA, DigiCert, GlobalSign, Sectigo,
/// internal Microsoft AD CS or OpenSSL <c>ts</c>, EJBCA — through the same code path.
/// </summary>
/// <remarks>
/// Replaces the old FreeTSA-specific provider. FreeTSA is still supported via the
/// <c>Stampd.Timestamp.FreeTsa</c> preset, which now wraps this class with FreeTSA defaults.
///
/// <para>
/// Authentication options (all optional, mix-and-match per TSA):
/// </para>
/// <list type="bullet">
///   <item><description>HTTP basic auth via <see cref="Rfc3161TimestampAuthorityOptions.BasicAuthUsername"/></description></item>
///   <item><description>Mutual TLS via <see cref="Rfc3161TimestampAuthorityOptions.ClientCertificatePkcs12Path"/></description></item>
/// </list>
///
/// <para>
/// Note on the client certificate path: when <see cref="Rfc3161TimestampAuthorityOptions.ClientCertificatePkcs12Path"/>
/// is set the caller must wire the cert into the <see cref="HttpClient"/>'s
/// <see cref="HttpClientHandler"/> at <see cref="IHttpClientFactory"/> registration time.
/// The DI helper in <see cref="Rfc3161ServiceCollectionExtensions"/> does this for you.
/// </para>
/// </remarks>
public sealed class Rfc3161TimestampAuthorityProvider : ITimestampAuthorityProvider
{
    private static readonly MediaTypeHeaderValue TimestampQueryContentType =
        MediaTypeHeaderValue.Parse("application/timestamp-query");

    private readonly HttpClient _httpClient;
    private readonly Rfc3161TimestampAuthorityOptions _options;
    private readonly AuthenticationHeaderValue? _basicAuthHeader;

    public Rfc3161TimestampAuthorityProvider(HttpClient httpClient, Rfc3161TimestampAuthorityOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _httpClient = httpClient;
        _options = options;
        _httpClient.Timeout = options.RequestTimeout;

        if (!string.IsNullOrEmpty(options.BasicAuthUsername))
        {
            var raw = $"{options.BasicAuthUsername}:{options.BasicAuthPassword}";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _basicAuthHeader = new AuthenticationHeaderValue("Basic", encoded);
        }
    }

    /// <inheritdoc />
    public string Name => _options.Name;

    /// <summary>Exposes the configured endpoint for logging / health-check messages.</summary>
    public Uri Endpoint => _options.Endpoint;

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
        requestGen.SetCertReq(_options.RequestTsaCertificate);
        if (!string.IsNullOrWhiteSpace(_options.RequestedPolicyOid))
        {
            requestGen.SetReqPolicy(new DerObjectIdentifier(_options.RequestedPolicyOid));
        }

        TimeStampRequest tspRequest = _options.IncludeNonce
            ? requestGen.Generate(tsaHashOid, hash, GenerateNonce())
            : requestGen.Generate(tsaHashOid, hash);

        var requestBytes = tspRequest.GetEncoded();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        using var requestContent = new ByteArrayContent(requestBytes);
        requestContent.Headers.ContentType = TimestampQueryContentType;
        httpRequest.Content = requestContent;
        if (_basicAuthHeader is not null)
        {
            httpRequest.Headers.Authorization = _basicAuthHeader;
        }

        using var httpResponse = await _httpClient
            .SendAsync(httpRequest, cancellationToken)
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
                $"TSA '{_options.Name}' at {_options.Endpoint} refused the timestamp request: " +
                $"status={tspResponse.Status.ToString(CultureInfo.InvariantCulture)}, " +
                $"failInfo={failInfo ?? "(none)"}, " +
                $"statusString={tspResponse.GetStatusString() ?? "(none)"}.");
        }

        return tspResponse.TimeStampToken.GetEncoded();
    }

    private static BigInteger GenerateNonce()
    {
        // 8 random bytes is plenty (RFC 3161 §2.4.1 — nonce is unique-per-request, not secret).
        Span<byte> buf = stackalloc byte[8];
        RandomNumberGenerator.Fill(buf);
        return new BigInteger(1, buf.ToArray());
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
            $"Rfc3161TimestampAuthorityProvider does not support hash algorithm '{algorithm.Name}'.");
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
            $"Rfc3161TimestampAuthorityProvider does not support hash algorithm '{algorithm.Name}'.");
    }
}
