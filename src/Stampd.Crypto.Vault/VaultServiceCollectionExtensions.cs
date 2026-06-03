using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.Vault;

public static class VaultServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="VaultSealingProvider"/> as the application's
    /// <see cref="ICryptographicSealingProvider"/>. Works against HashiCorp Vault and
    /// OpenBao via the shared HTTP API.
    /// </summary>
    public static IServiceCollection AddVaultSealing(
        this IServiceCollection services,
        Action<VaultSealingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<ICryptographicSealingProvider>(_ =>
        {
            var options = new VaultSealingOptions();
            configure(options);
            return new VaultSealingProvider(options);
        });

        return services;
    }
}
