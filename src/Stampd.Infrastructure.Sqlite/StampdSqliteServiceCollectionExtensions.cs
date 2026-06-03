using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Infrastructure.DependencyInjection;

namespace Stampd.Infrastructure.Sqlite;

/// <summary>
/// Wires the Stampd DbContext onto SQLite. Migrations live in this assembly.
/// </summary>
public static class StampdSqliteServiceCollectionExtensions
{
    public static IServiceCollection AddStampdSqlite(
        this IServiceCollection services,
        string connectionString,
        Action<Microsoft.EntityFrameworkCore.Infrastructure.SqliteDbContextOptionsBuilder>? configureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddStampdDbContext(options =>
        {
            options.UseSqlite(connectionString, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(StampdSqliteServiceCollectionExtensions).Assembly.GetName().Name);
                configureProvider?.Invoke(sqlite);
            });
        });
    }
}
