using Microsoft.Extensions.Diagnostics.HealthChecks;

using Stampd.Core.Sealing;

namespace Stampd.WebApi.Observability.HealthChecks;

/// <summary>
/// Confirms the configured <see cref="ICryptographicSealingProvider"/> can produce its
/// signing certificate. For LocalCertificate this is a fast in-memory check; for HSM
/// providers it does a real round-trip to verify connectivity and auth.
/// </summary>
internal sealed class SealingProviderHealthCheck : IHealthCheck
{
    private readonly ICryptographicSealingProvider _provider;

    public SealingProviderHealthCheck(ICryptographicSealingProvider provider)
    {
        _provider = provider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cert = await _provider.GetSigningCertificateAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                description: $"Sealing provider '{_provider.Name}' returned certificate {cert.Thumbprint}.",
                data: new Dictionary<string, object>
                {
                    ["providerName"] = _provider.Name,
                    ["certificateThumbprint"] = cert.Thumbprint,
                    ["notAfter"] = cert.NotAfter,
                });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: $"Sealing provider '{_provider.Name}' failed: {ex.Message}",
                exception: ex);
        }
    }
}
