using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Identity;

namespace Stampd.Identity.EmailOtp;

public static class EmailOtpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EmailOtpProvider"/> as the application's
    /// <see cref="IIdentityVerificationProvider"/>, using an in-memory challenge store.
    /// </summary>
    /// <remarks>
    /// For multi-node production deployments, also register a custom <see cref="IOtpChallengeStore"/>
    /// (Redis, SQL Server, etc.) BEFORE calling this — the registration is idempotent
    /// via <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>.
    /// </remarks>
    public static IServiceCollection AddEmailOtpIdentityVerification(
        this IServiceCollection services,
        Action<EmailOtpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new EmailOtpOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IOtpChallengeStore, InMemoryOtpChallengeStore>();
        services.TryAddSingleton<IIdentityVerificationProvider, EmailOtpProvider>();

        return services;
    }
}
