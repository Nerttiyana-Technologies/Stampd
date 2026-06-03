using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Sealing;

namespace Stampd.Timestamp.FreeTsa;

/// <summary>
/// DI registration helpers for <see cref="FreeTsaTimestampAuthorityProvider"/>.
/// </summary>
public static class FreeTsaServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FreeTsaTimestampAuthorityProvider"/> as the application's
    /// <see cref="ITimestampAuthorityProvider"/>, using a named <see cref="HttpClient"/>
    /// via <c>IHttpClientFactory</c> so connection pooling and Polly policies are
    /// applied consistently.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">
    /// Optional TSA endpoint override. Defaults to <see cref="FreeTsaTimestampAuthorityProvider.DefaultEndpoint"/>
    /// (<c>https://freetsa.org/tsr</c>).
    /// </param>
    public static IServiceCollection AddFreeTsaTimestampAuthority(
        this IServiceCollection services,
        Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(FreeTsaTimestampAuthorityProvider));

        services.TryAddSingleton<ITimestampAuthorityProvider>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(nameof(FreeTsaTimestampAuthorityProvider));
            return new FreeTsaTimestampAuthorityProvider(client, endpoint);
        });

        return services;
    }
}
