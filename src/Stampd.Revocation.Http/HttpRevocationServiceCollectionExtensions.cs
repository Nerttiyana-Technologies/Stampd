using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Stampd.Core.Revocation;

namespace Stampd.Revocation.Http;

/// <summary>
/// DI helpers for the HTTP-based OCSP + CRL revocation providers.
/// </summary>
public static class HttpRevocationServiceCollectionExtensions
{
    /// <summary>
    /// Registers OCSP + CRL HTTP providers wrapped in a
    /// <see cref="CompositeRevocationProvider"/> that fetches both sources when available.
    /// </summary>
    public static IServiceCollection AddHttpRevocationProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(OcspHttpRevocationProvider))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(nameof(CrlHttpRevocationProvider))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        services.TryAddSingleton<IRevocationProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var ocsp = new OcspHttpRevocationProvider(
                factory.CreateClient(nameof(OcspHttpRevocationProvider)),
                loggerFactory?.CreateLogger<OcspHttpRevocationProvider>());
            var crl = new CrlHttpRevocationProvider(
                factory.CreateClient(nameof(CrlHttpRevocationProvider)),
                loggerFactory?.CreateLogger<CrlHttpRevocationProvider>());
            return new CompositeRevocationProvider([ocsp, crl], continueOnSuccess: true);
        });

        return services;
    }
}
