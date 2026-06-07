using Microsoft.AspNetCore.Mvc.Testing;

namespace Stampd.WebApi.Tests;

/// <summary>
/// Spins up the real Stampd.WebApi host with overridden config so tests don't touch the
/// developer's <c>~/.stampd</c>, hit FreeTSA, or talk to Vault.
/// </summary>
public sealed class StampdWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _isolatedRoot;

    public StampdWebApplicationFactory()
    {
        _isolatedRoot = Path.Combine(Path.GetTempPath(), $"stampd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_isolatedRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

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
        });

        base.ConfigureWebHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
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
