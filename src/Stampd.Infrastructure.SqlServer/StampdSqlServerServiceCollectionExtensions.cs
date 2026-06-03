using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Infrastructure.DependencyInjection;

namespace Stampd.Infrastructure.SqlServer;

/// <summary>
/// Wires the Stampd DbContext onto Microsoft SQL Server. Migrations live in this assembly.
/// </summary>
public static class StampdSqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StampdDbContext"/> against Microsoft SQL Server using the
    /// supplied connection string. Migrations are sourced from this assembly.
    /// </summary>
    public static IServiceCollection AddStampdSqlServer(
        this IServiceCollection services,
        string connectionString,
        Action<Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder>? configureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddStampdDbContext(options =>
        {
            options.UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsAssembly(typeof(StampdSqlServerServiceCollectionExtensions).Assembly.GetName().Name);
                sqlServer.EnableRetryOnFailure(maxRetryCount: 3);
                configureProvider?.Invoke(sqlServer);
            });
        });
    }
}
