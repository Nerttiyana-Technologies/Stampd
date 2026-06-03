using Azure.Core;
using Azure.Identity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.AzureKeyVault;

public static class AzureKeyVaultServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AzureKeyVaultSealingProvider"/> as the application's
    /// <see cref="ICryptographicSealingProvider"/>. Uses <see cref="DefaultAzureCredential"/>
    /// for auth — works locally via Azure CLI / VS Code, in CI via OIDC federation, and
    /// in production via Managed Identity.
    /// </summary>
    public static IServiceCollection AddAzureKeyVaultSealing(
        this IServiceCollection services,
        Action<AzureKeyVaultSealingOptions> configure,
        Func<IServiceProvider, TokenCredential>? credentialFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<ICryptographicSealingProvider>(sp =>
        {
            var options = new AzureKeyVaultSealingOptions();
            configure(options);
            var credential = credentialFactory?.Invoke(sp) ?? new DefaultAzureCredential();
            return new AzureKeyVaultSealingProvider(options, credential);
        });

        return services;
    }
}
