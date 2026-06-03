using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Stampd.Core.Tenancy;

namespace Stampd.Infrastructure.Postgres;

/// <summary>
/// Allows <c>dotnet ef</c> to construct a <see cref="StampdDbContext"/> at design time
/// without booting the host application.
/// </summary>
/// <remarks>
/// Set the <c>STAMPD_POSTGRES_CONNECTION</c> environment variable to override the default
/// connection string. The default targets a local PostgreSQL instance on port 5432.
/// </remarks>
public sealed class StampdPostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<StampdDbContext>
{
    public StampdDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("STAMPD_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=stampd_designtime;Username=postgres;Password=postgres";

        var builder = new DbContextOptionsBuilder<StampdDbContext>();
        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsAssembly(typeof(StampdPostgresDesignTimeDbContextFactory).Assembly.GetName().Name));

        var tenantContext = new AmbientTenantContext(Guid.Empty);

        return new StampdDbContext(builder.Options, tenantContext);
    }
}
