using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Identity;

namespace Stampd.Infrastructure.Identity;

/// <summary>
/// Wiring for the EF Core-backed persistent OTP challenge store.
/// </summary>
public static class DbOtpChallengeStoreServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the default in-memory OTP challenge store with a persistent EF Core-backed
    /// store. Call BEFORE <c>AddEmailOtpIdentityVerification</c> (or any other OTP-channel
    /// extension) — those use <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>,
    /// so whichever <see cref="IOtpChallengeStore"/> is registered first wins.
    /// </summary>
    public static IServiceCollection AddDbOtpChallengeStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOtpChallengeStore, DbOtpChallengeStore>();
        return services;
    }
}
