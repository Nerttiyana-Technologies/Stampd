using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Stampd.Core.Tenancy;

namespace Stampd.Infrastructure.Sqlite;

/// <summary>
/// Allows <c>dotnet ef</c> to construct a <see cref="StampdDbContext"/> at design time.
/// </summary>
/// <remarks>
/// Set <c>STAMPD_SQLITE_CONNECTION</c> to override the default (a file under the user's
/// home directory).
/// </remarks>
public sealed class StampdSqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<StampdDbContext>
{
    public StampdDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("STAMPD_SQLITE_CONNECTION")
            ?? $"Data Source={Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".stampd",
                "stampd-designtime.db")}";

        var builder = new DbContextOptionsBuilder<StampdDbContext>();
        builder.UseSqlite(connectionString, sqlite =>
            sqlite.MigrationsAssembly(typeof(StampdSqliteDesignTimeDbContextFactory).Assembly.GetName().Name));

        var tenantContext = new AmbientTenantContext(Guid.Empty);
        return new StampdDbContext(builder.Options, tenantContext);
    }
}
