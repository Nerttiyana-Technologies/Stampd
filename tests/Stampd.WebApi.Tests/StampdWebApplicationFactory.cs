using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Stampd.Infrastructure;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Spins up the real Stampd.WebApi host with overridden config so tests don't touch the
/// developer's <c>~/.stampd</c>, hit FreeTSA, or talk to Vault.
/// </summary>
public class StampdWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _isolatedRoot;
    // v3.0 alpha.1 — held open for the lifetime of the factory so the in-memory
    // SQLite database doesn't disappear when EF Core releases its transient
    // connection. Each factory instance gets its own connection, so per-test
    // state is fully isolated (the file-path approach was being overridden by
    // some other config source we couldn't trace — a private :memory: connection
    // is bulletproof).
    private readonly SqliteConnection _dbConnection;

    /// <summary>
    /// Single public parameterless ctor — xUnit's <c>IClassFixture&lt;T&gt;</c>
    /// requires exactly one public ctor on the fixture type and resolves it
    /// reflectively. Per-test config overrides ride on the
    /// <see cref="ScopedStampdWebApplicationFactory"/> subclass (v3.0 alpha.1+),
    /// which the tests that need it instantiate directly.
    /// </summary>
    public StampdWebApplicationFactory()
    {
        _isolatedRoot = Path.Combine(Path.GetTempPath(), $"stampd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_isolatedRoot);
        _dbConnection = new SqliteConnection("Data Source=:memory:");
        _dbConnection.Open();
    }

    /// <summary>
    /// Subclass extension point — return additional key/value pairs to layer on
    /// top of the default in-memory baseline. Called once per host build.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?>? ExtraConfig() => null;

    /// <summary>
    /// Subclass extension point — return the ASP.NET Core environment name. v3.0
    /// alpha.2 uses this so the auth-mode startup guard can be tested with
    /// "Production" without changing the rest of the test infrastructure.
    /// Defaults to "Testing".
    /// </summary>
    protected virtual string EnvironmentName() => "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName());

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Force LocalCertificate sealing — no Vault dependency in tests
                ["Stampd:Sealing:Provider"] = "Local",
                ["Stampd:SigningCertificate:Pkcs12Path"] = Path.Combine(_isolatedRoot, "test-signer.pfx"),
                ["Stampd:SigningCertificate:Password"] = "stampd-test",

                // No TSA — tests stay fully offline
                ["Stampd:Tsa:Enabled"] = "false",

                // Isolated storage and DB
                ["Stampd:Storage:FileSystemRoot"] = Path.Combine(_isolatedRoot, "documents"),
                ["Stampd:Database:SqlitePath"] = Path.Combine(_isolatedRoot, "stampd-tests.db"),

                // Deterministic JWT signing key for token issuance + validation
                ["Stampd:Auth:Jwt:SigningKey"] = "stampd-test-signing-key-do-not-use-in-prod-32+chars",
                ["Stampd:Auth:Jwt:Issuer"] = "stampd-test",
                ["Stampd:Auth:Jwt:Audience"] = "stampd-test-api",
                ["Stampd:Auth:Jwt:EnableDevTokenEndpoint"] = "true",

                // Log to /dev/null during tests
                ["Stampd:Logging:Directory"] = Path.Combine(_isolatedRoot, "logs"),
            });

            // v3.0 alpha.1 — subclasses can layer additional overrides on top of
            // the baseline above. Added last so subclass keys take precedence.
            var extras = ExtraConfig();
            if (extras is { Count: > 0 })
            {
                config.AddInMemoryCollection(extras);
            }
        });

        // v3.0 alpha.1 — replace whatever DbContextOptions<StampdDbContext>
        // Program.cs registered with one bound to our held-open in-memory
        // connection. Then EnsureCreated so the schema lands without needing
        // the per-provider migration assembly to know about an in-memory path.
        builder.ConfigureTestServices(services =>
        {
            var optionsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<StampdDbContext>));
            if (optionsDescriptor is not null)
            {
                services.Remove(optionsDescriptor);
            }

            services.AddDbContext<StampdDbContext>(options =>
                options.UseSqlite(_dbConnection));
        });

        base.ConfigureWebHost(builder);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // EnsureCreated builds the full model schema on the in-memory connection
        // on first build. Skipping the migration history table since :memory:
        // doesn't need provenance and EF's migration scaffolding doesn't ship
        // an in-memory variant.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
        db.Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Close the connection first — once it closes, the in-memory DB
            // is gone too, which is exactly what we want.
            _dbConnection.Dispose();
        }
        base.Dispose(disposing);
        try
        {
            if (disposing && Directory.Exists(_isolatedRoot))
            {
                Directory.Delete(_isolatedRoot, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
