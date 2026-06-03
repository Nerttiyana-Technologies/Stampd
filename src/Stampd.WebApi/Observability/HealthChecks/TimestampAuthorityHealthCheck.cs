using Microsoft.Extensions.Diagnostics.HealthChecks;

using Stampd.Core.Sealing;

namespace Stampd.WebApi.Observability.HealthChecks;

/// <summary>
/// Optional TSA reachability check. Sends a synthetic minimal hash + best-effort timestamp
/// request to the configured TSA. Marked Degraded (not Unhealthy) on failure since signing
/// without timestamping is still possible at PAdES B-B level.
/// </summary>
internal sealed class TimestampAuthorityHealthCheck : IHealthCheck
{
    private readonly ITimestampAuthorityProvider? _provider;

    public TimestampAuthorityHealthCheck(ITimestampAuthorityProvider? provider = null)
    {
        _provider = provider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_provider is null)
        {
            return HealthCheckResult.Healthy("No TSA configured (PAdES B-B mode).");
        }

        try
        {
            var probeData = "stampd-health-probe"u8.ToArray();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var tst = await _provider
                .RequestTimestampAsync(probeData, System.Security.Cryptography.HashAlgorithmName.SHA256, cts.Token)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy(
                description: $"TSA '{_provider.Name}' returned a {tst.Length}-byte token in time.",
                data: new Dictionary<string, object>
                {
                    ["providerName"] = _provider.Name,
                    ["tokenSize"] = tst.Length,
                });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded(
                description: $"TSA '{_provider.Name}' unreachable: {ex.Message}. Signing will still work but only at PAdES B-B level.",
                exception: ex);
        }
    }
}
