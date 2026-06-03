using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Identity;

namespace Stampd.Identity.SmsOtp;

public static class SmsOtpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SmsOtpProvider"/> as the application's
    /// <see cref="IIdentityVerificationProvider"/>. Defaults to an in-memory
    /// <see cref="InMemoryOtpChallengeStore"/> and a <see cref="MockSmsGateway"/> — wire
    /// a real <see cref="ISmsGateway"/> (Twilio, SNS) and a persistent store BEFORE calling
    /// this for production deployments.
    /// </summary>
    public static IServiceCollection AddSmsOtpIdentityVerification(
        this IServiceCollection services,
        Action<SmsOtpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SmsOtpOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IOtpChallengeStore, InMemoryOtpChallengeStore>();
        services.TryAddSingleton<ISmsGateway, MockSmsGateway>();
        services.TryAddSingleton<IIdentityVerificationProvider, SmsOtpProvider>();

        return services;
    }
}
