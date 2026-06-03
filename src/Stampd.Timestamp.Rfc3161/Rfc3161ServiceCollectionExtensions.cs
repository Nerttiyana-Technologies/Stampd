using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Stampd.Core.Sealing;

namespace Stampd.Timestamp.Rfc3161;

/// <summary>
/// DI registration helpers for the generic <see cref="Rfc3161TimestampAuthorityProvider"/>.
/// </summary>
public static class Rfc3161ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="Rfc3161TimestampAuthorityProvider"/> as the application's
    /// <see cref="ITimestampAuthorityProvider"/>. Wires a named <see cref="HttpClient"/>
    /// via <c>IHttpClientFactory</c> with mTLS client cert support when configured.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to populate <see cref="Rfc3161TimestampAuthorityOptions"/>.</param>
    public static IServiceCollection AddRfc3161TimestampAuthority(
        this IServiceCollection services,
        Action<Rfc3161TimestampAuthorityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddHttpClient(nameof(Rfc3161TimestampAuthorityProvider))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<Rfc3161TimestampAuthorityOptions>>().Value;
                var handler = new HttpClientHandler();

                if (!string.IsNullOrEmpty(options.ClientCertificatePkcs12Path))
                {
                    // mTLS path: load the PKCS#12 and attach to the handler. Used by internal
                    // TSAs like Microsoft AD CS and EJBCA.
                    var clientCert = X509CertificateLoader.LoadPkcs12FromFile(
                        options.ClientCertificatePkcs12Path,
                        options.ClientCertificatePassword);
                    handler.ClientCertificates.Add(clientCert);
                    handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                }

                return handler;
            });

        services.TryAddSingleton<ITimestampAuthorityProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(nameof(Rfc3161TimestampAuthorityProvider));
            var options = sp.GetRequiredService<IOptions<Rfc3161TimestampAuthorityOptions>>().Value;
            return new Rfc3161TimestampAuthorityProvider(client, options);
        });

        return services;
    }
}
