using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.LocalCertificate;

/// <summary>
/// DI registration helpers for <see cref="LocalCertificateSealingProvider"/>.
/// </summary>
public static class LocalCertificateServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="LocalCertificateSealingProvider"/> backed by the supplied
    /// certificate as the application's <see cref="ICryptographicSealingProvider"/>.
    /// </summary>
    public static IServiceCollection AddLocalCertificateSealing(
        this IServiceCollection services,
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(certificate);

        services.TryAddSingleton<ICryptographicSealingProvider>(
            _ => new LocalCertificateSealingProvider(certificate));

        return services;
    }

    /// <summary>
    /// Registers a <see cref="LocalCertificateSealingProvider"/> using a factory the
    /// caller supplies. Useful when the certificate is loaded from a secret store at
    /// startup.
    /// </summary>
    public static IServiceCollection AddLocalCertificateSealing(
        this IServiceCollection services,
        Func<IServiceProvider, X509Certificate2> certificateFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(certificateFactory);

        services.TryAddSingleton<ICryptographicSealingProvider>(
            sp => new LocalCertificateSealingProvider(certificateFactory(sp)));

        return services;
    }
}
