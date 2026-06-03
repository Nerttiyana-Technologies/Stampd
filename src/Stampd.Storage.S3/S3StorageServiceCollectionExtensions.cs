using Amazon.S3;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Storage;

namespace Stampd.Storage.S3;

public static class S3StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="S3DocumentStorageProvider"/> as the application's
    /// <see cref="IDocumentStorageProvider"/>. Auth uses the AWS SDK's default credential
    /// resolution chain.
    /// </summary>
    public static IServiceCollection AddS3DocumentStorage(
        this IServiceCollection services,
        Action<S3DocumentStorageOptions> configure,
        Func<IServiceProvider, IAmazonS3>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IDocumentStorageProvider>(sp =>
        {
            var options = new S3DocumentStorageOptions();
            configure(options);
            var s3 = clientFactory?.Invoke(sp);
            return new S3DocumentStorageProvider(options, s3);
        });

        return services;
    }
}
