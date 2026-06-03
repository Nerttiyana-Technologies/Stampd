using Azure.Core;
using Azure.Identity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Storage;

namespace Stampd.Storage.AzureBlob;

public static class AzureBlobStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AzureBlobDocumentStorageProvider"/> as the application's
    /// <see cref="IDocumentStorageProvider"/>. Defaults to <see cref="DefaultAzureCredential"/>
    /// for auth.
    /// </summary>
    public static IServiceCollection AddAzureBlobDocumentStorage(
        this IServiceCollection services,
        Action<AzureBlobDocumentStorageOptions> configure,
        Func<IServiceProvider, TokenCredential>? credentialFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IDocumentStorageProvider>(sp =>
        {
            var options = new AzureBlobDocumentStorageOptions();
            configure(options);
            var credential = credentialFactory?.Invoke(sp) ?? new DefaultAzureCredential();
            return new AzureBlobDocumentStorageProvider(options, credential);
        });

        return services;
    }
}
