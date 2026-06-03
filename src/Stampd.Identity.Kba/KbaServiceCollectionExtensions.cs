using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Identity;

namespace Stampd.Identity.Kba;

public static class KbaServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="KbaIdentityVerificationProvider"/> as the application's
    /// <see cref="IIdentityVerificationProvider"/>. Defaults to <see cref="MockKbaProvider"/>
    /// — adopters wire a real <see cref="IKbaProvider"/> (LexisNexis, Experian, ID.me) by
    /// registering their implementation BEFORE calling this.
    /// </summary>
    public static IServiceCollection AddKbaIdentityVerification(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IKbaProvider, MockKbaProvider>();
        services.TryAddSingleton<IIdentityVerificationProvider, KbaIdentityVerificationProvider>();

        return services;
    }
}
