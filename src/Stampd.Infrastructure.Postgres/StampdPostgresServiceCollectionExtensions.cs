using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Infrastructure.DependencyInjection;

namespace Stampd.Infrastructure.Postgres;

/// <summary>
/// Wires the Stampd DbContext onto PostgreSQL via Npgsql. Migrations live in this assembly.
/// </summary>
public static class StampdPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StampdDbContext"/> against PostgreSQL using the supplied
    /// connection string. Migrations are sourced from this assembly and will use
    /// PostgreSQL-native column types (e.g. <c>jsonb</c> for the audit payload) where the
    /// Npgsql provider's defaults yield them.
    /// </summary>
    public static IServiceCollection AddStampdPostgres(
        this IServiceCollection services,
        string connectionString,
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? configureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddStampdDbContext(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(StampdPostgresServiceCollectionExtensions).Assembly.GetName().Name);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
                configureProvider?.Invoke(npgsql);
            });
        });
    }
}
