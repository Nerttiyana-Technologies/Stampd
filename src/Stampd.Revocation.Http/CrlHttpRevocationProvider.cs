using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Stampd.Core.Revocation;

namespace Stampd.Revocation.Http;

/// <summary>
/// Fetches a Certificate Revocation List (CRL) for a certificate by reading the cert's
/// CRL Distribution Points (CDP) extension and GETting the first reachable URL.
/// </summary>
/// <remarks>
/// CRLs can be large (MB-scale for big CAs). Use OCSP first via
/// <see cref="OcspHttpRevocationProvider"/> when available; fall back to this provider for
/// CAs without OCSP responders. <see cref="CompositeRevocationProvider"/> wires this
/// fallback automatically.
///
/// Returns <see cref="CertificateRevocationInfo.Empty"/> on missing extension or unreachable
/// URLs; never throws on network errors.
/// </remarks>
public sealed class CrlHttpRevocationProvider : IRevocationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CrlHttpRevocationProvider> _logger;

    public CrlHttpRevocationProvider(
        HttpClient httpClient,
        ILogger<CrlHttpRevocationProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<CrlHttpRevocationProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => "CRL-HTTP";

    /// <inheritdoc />
    public async Task<CertificateRevocationInfo> GetRevocationInfoAsync(
        X509Certificate2 certificate,
        X509Certificate2 issuer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var crlUrls = CertificateExtensionExtractor.GetCrlUrls(certificate);
        if (crlUrls.Count == 0)
        {
            _logger.LogDebug(
                "Certificate {Thumbprint} has no CRL distribution point in its CDP extension.",
                certificate.Thumbprint);
            return CertificateRevocationInfo.Empty;
        }

        foreach (var url in crlUrls)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var crlBytes = await response.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                return new CertificateRevocationInfo(OcspResponse: null, Crl: crlBytes);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "CRL fetch from {Url} failed.", url);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogDebug(ex, "CRL fetch from {Url} timed out.", url);
            }
        }

        return CertificateRevocationInfo.Empty;
    }
}
