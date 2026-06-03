using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Stampd.Infrastructure.DependencyInjection;

/// <summary>
/// Provider-agnostic registration helpers for the Stampd DbContext. The SQL Server and
/// Postgres provider projects build on top of these.
/// </summary>
public static class StampdInfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StampdDbContext"/> with the supplied <see cref="DbContextOptionsBuilder"/>
    /// configuration callback. Callers are responsible for configuring the database
    /// provider (SQL Server, PostgreSQL) inside the callback.
    /// </summary>
    /// <remarks>
    /// The consumer is also responsible for registering an <see cref="Stampd.Core.Tenancy.ITenantContext"/>
    /// implementation in the service collection. Without one, the DbContext will fail to
    /// resolve at request time.
    /// </remarks>
    public static IServiceCollection AddStampdDbContext(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddDbContext<StampdDbContext>(configureOptions);
        return services;
    }
}
