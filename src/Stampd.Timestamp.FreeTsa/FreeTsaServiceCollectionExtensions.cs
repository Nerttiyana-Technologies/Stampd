using Microsoft.Extensions.DependencyInjection;

using Stampd.Timestamp.Rfc3161;

namespace Stampd.Timestamp.FreeTsa;

/// <summary>
/// DI registration helpers for the FreeTSA preset.
/// </summary>
public static class FreeTsaServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="Rfc3161TimestampAuthorityProvider"/> preconfigured for
    /// FreeTSA. Equivalent to calling <c>AddRfc3161TimestampAuthority</c> with FreeTSA's
    /// endpoint and a "FreeTSA" provider name.
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

        return services.AddRfc3161TimestampAuthority(options =>
        {
            options.Name = "FreeTSA";
            options.Endpoint = endpoint ?? FreeTsaTimestampAuthorityProvider.DefaultEndpoint;
            options.RequestTsaCertificate = true;
            options.IncludeNonce = true;
        });
    }
}
