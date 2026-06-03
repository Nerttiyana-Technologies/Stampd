using Amazon.KeyManagementService;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Sealing;

namespace Stampd.Crypto.AwsKms;

public static class AwsKmsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AwsKmsSealingProvider"/> as the application's
    /// <see cref="ICryptographicSealingProvider"/>. Authentication uses the AWS SDK's
    /// default credential resolution chain (environment variables, shared profile, EC2
    /// instance metadata, ECS task role, EKS IRSA / Pod Identity).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to populate <see cref="AwsKmsSealingOptions"/>.</param>
    /// <param name="clientFactory">
    /// Optional factory to override the KMS client. Use this to inject a pre-configured
    /// client (e.g. with custom retry policy, endpoint override for KMS-emulator tests).
    /// </param>
    public static IServiceCollection AddAwsKmsSealing(
        this IServiceCollection services,
        Action<AwsKmsSealingOptions> configure,
        Func<IServiceProvider, IAmazonKeyManagementService>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<ICryptographicSealingProvider>(sp =>
        {
            var options = new AwsKmsSealingOptions();
            configure(options);
            var kmsClient = clientFactory?.Invoke(sp);
            return new AwsKmsSealingProvider(options, kmsClient);
        });

        return services;
    }
}
