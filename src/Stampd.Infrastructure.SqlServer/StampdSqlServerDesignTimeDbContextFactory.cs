using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Stampd.Core.Tenancy;

namespace Stampd.Infrastructure.SqlServer;

/// <summary>
/// Allows <c>dotnet ef</c> to construct a <see cref="StampdDbContext"/> at design time
/// without booting the host application.
/// </summary>
/// <remarks>
/// Set the <c>STAMPD_SQLSERVER_CONNECTION</c> environment variable to override the default
/// connection string. The default targets SQL Server Express LocalDB (Windows only); macOS
/// and Linux developers should set the env var to a containerised SQL Server endpoint
/// (e.g. <c>Server=localhost,1433;Database=StampdDesignTime;User Id=sa;Password=...;TrustServerCertificate=True</c>).
/// </remarks>
public sealed class StampdSqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<StampdDbContext>
{
    public StampdDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("STAMPD_SQLSERVER_CONNECTION")
            ?? @"Server=(localdb)\mssqllocaldb;Database=StampdDesignTime;Trusted_Connection=True;TrustServerCertificate=True";

        var builder = new DbContextOptionsBuilder<StampdDbContext>();
        builder.UseSqlServer(connectionString, sql =>
            sql.MigrationsAssembly(typeof(StampdSqlServerDesignTimeDbContextFactory).Assembly.GetName().Name));

        // Design-time scenarios are inherently cross-tenant.
        var tenantContext = new AmbientTenantContext(Guid.Empty);

        return new StampdDbContext(builder.Options, tenantContext);
    }
}
