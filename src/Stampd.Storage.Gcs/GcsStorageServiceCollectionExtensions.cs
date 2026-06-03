using Google.Cloud.Storage.V1;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Storage;

namespace Stampd.Storage.Gcs;

public static class GcsStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GcsDocumentStorageProvider"/> as the application's
    /// <see cref="IDocumentStorageProvider"/>. Auth uses Application Default Credentials.
    /// </summary>
    public static IServiceCollection AddGcsDocumentStorage(
        this IServiceCollection services,
        Action<GcsDocumentStorageOptions> configure,
        Func<IServiceProvider, StorageClient>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IDocumentStorageProvider>(sp =>
        {
            var options = new GcsDocumentStorageOptions();
            configure(options);
            var client = clientFactory?.Invoke(sp);
            return new GcsDocumentStorageProvider(options, client);
        });

        return services;
    }
}
