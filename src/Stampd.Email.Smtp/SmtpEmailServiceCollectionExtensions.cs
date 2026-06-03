using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Stampd.Core.Notifications;

namespace Stampd.Email.Smtp;

public static class SmtpEmailServiceCollectionExtensions
{
    public static IServiceCollection AddSmtpEmailSender(
        this IServiceCollection services,
        Action<SmtpEmailSenderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IEmailSender>(_ =>
        {
            var options = new SmtpEmailSenderOptions();
            configure(options);
            return new SmtpEmailSender(options);
        });

        return services;
    }
}
