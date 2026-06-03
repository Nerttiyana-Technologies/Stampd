using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

using Stampd.Core.Revocation;

namespace Stampd.Revocation.Http;

/// <summary>
/// Fetches an OCSP response for a certificate by reading the cert's AIA extension and
/// POSTing an RFC 6960 OCSP request to the responder.
/// </summary>
/// <remarks>
/// Returns <see cref="CertificateRevocationInfo.Empty"/> (with both fields null) when:
/// <list type="bullet">
///   <item><description>The cert has no AIA extension or no OCSP responder URL.</description></item>
///   <item><description>Every responder URL returns an error or unreachable.</description></item>
/// </list>
///
/// Does NOT throw on network errors — revocation lookups are best-effort. Callers can chain
/// this with a CRL fallback via <see cref="CompositeRevocationProvider"/>.
/// </remarks>
public sealed class OcspHttpRevocationProvider : IRevocationProvider
{
    private static readonly MediaTypeHeaderValue OcspRequestContentType =
        MediaTypeHeaderValue.Parse("application/ocsp-request");

    private readonly HttpClient _httpClient;
    private readonly ILogger<OcspHttpRevocationProvider> _logger;

    public OcspHttpRevocationProvider(
        HttpClient httpClient,
        ILogger<OcspHttpRevocationProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<OcspHttpRevocationProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => "OCSP-HTTP";

    /// <inheritdoc />
    public async Task<CertificateRevocationInfo> GetRevocationInfoAsync(
        X509Certificate2 certificate,
        X509Certificate2 issuer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(issuer);

        var ocspUrls = CertificateExtensionExtractor.GetOcspUrls(certificate);
        if (ocspUrls.Count == 0)
        {
            _logger.LogDebug(
                "Certificate {Thumbprint} has no OCSP responder URL in its AIA extension.",
                certificate.Thumbprint);
            return CertificateRevocationInfo.Empty;
        }

        var requestBytes = BuildOcspRequest(certificate, issuer);

        foreach (var url in ocspUrls)
        {
            try
            {
                var responseBytes = await PostOcspRequestAsync(url, requestBytes, cancellationToken)
                    .ConfigureAwait(false);

                // Validate that the response parses and indicates 'successful' (status code 0).
                // Other statuses (malformedRequest, internalError, tryLater, sigRequired, unauthorized)
                // are skipped — try the next URL.
                var ocspResp = new OcspResp(responseBytes);
                if (ocspResp.Status != OcspRespStatus.Successful)
                {
                    _logger.LogDebug(
                        "OCSP responder at {Url} returned non-success status {Status}.",
                        url,
                        ocspResp.Status);
                    continue;
                }

                return new CertificateRevocationInfo(OcspResponse: responseBytes, Crl: null);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "OCSP fetch from {Url} failed.", url);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogDebug(ex, "OCSP fetch from {Url} timed out.", url);
            }
            catch (OcspException ex)
            {
                _logger.LogDebug(ex, "OCSP response from {Url} failed to parse.", url);
            }
        }

        return CertificateRevocationInfo.Empty;
    }

    private async Task<byte[]> PostOcspRequestAsync(Uri url, byte[] requestBytes, CancellationToken ct)
    {
        using var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = OcspRequestContentType;

        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static byte[] BuildOcspRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var bcCert = DotNetUtilities.FromX509Certificate(certificate);
        var bcIssuer = DotNetUtilities.FromX509Certificate(issuer);

        var generator = new OcspReqGenerator();
        // SHA-1 is intentional here: RFC 6960 still mandates SHA-1 for CertID.hashAlgorithm.
        // The wider OCSP response signature uses the responder's chosen algorithm — usually
        // SHA-256 — so this isn't a security regression.
        var certId = new CertificateID(
            new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1),
            bcIssuer,
            bcCert.SerialNumber);
        generator.AddRequest(certId);

        // No nonce by default — some real-world responders (DigiCert, Let's Encrypt for OCSP
        // stapling) reject requests with nonces. Adopters who need a nonce can wrap this
        // provider and customize.
        var ocspRequest = generator.Generate();
        return ocspRequest.GetEncoded();
    }
}
