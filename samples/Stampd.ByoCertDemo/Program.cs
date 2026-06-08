// Stampd.ByoCertDemo — bring-your-own-cert customer demo (Blazor Server).
//
// Boots a single-page wizard at http://localhost:5180/. A customer drops in
// their PFX + password, the cert is encrypted at rest via ASP.NET Core's
// Data Protection (AES-256-GCM, managed key ring), and then they can upload
// PDFs to sign whenever they come back.
//
// Storage layout (defaults; override via Stampd:ByoCertDemo:VaultPath):
//   ~/.stampd/byo-cert-demo/
//     keys/   - Data Protection key ring
//     vault/  - {guid}.cert (encrypted PFX+password) + {guid}.meta (plaintext metadata)
//
// No external dependencies. Runs on a demo laptop with `dotnet run`.

using Microsoft.AspNetCore.DataProtection;

using Stampd.ByoCertDemo.Components;
using Stampd.ByoCertDemo.Services;
using Stampd.Engine.Rendering;

// PdfSharp 6.x has no default font resolver outside Windows; register ours so
// the engine can draw the signed-by name and date.
PlatformFontResolver.Register();

var builder = WebApplication.CreateBuilder(args);

// -- File logging ------------------------------------------------------------
// Append-only rolling log to ~/.stampd/byo-cert-demo/logs/demo-<timestamp>.log
// alongside the normal console output. Captures the full failover trace —
// every TSA attempt, every exception, every aggregated failure summary —
// so a flaky FreeTSA or a blocked DigiCert leaves a permanent breadcrumb the
// presenter can grep after the demo. Path is printed at boot.
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".stampd", "byo-cert-demo", "logs");
var fileLogProvider = new Stampd.ByoCertDemo.Services.FileLoggerProvider(logDir);
builder.Logging.AddProvider(fileLogProvider);
builder.Logging.SetMinimumLevel(LogLevel.Information);
Console.WriteLine($"[Stampd.ByoCertDemo] Logging to {fileLogProvider.FullPath}");

// -- Data Protection ---------------------------------------------------------
// Key ring lives next to the vault directory. Same-machine persistence — keys
// survive restarts but don't roam. Customers in a demo demand persistence; a
// roaming key ring (e.g. Azure Blob + KV) is the production extension.
var vaultRoot = builder.Configuration["Stampd:ByoCertDemo:VaultPath"]
                ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       ".stampd", "byo-cert-demo");
var keyRingDir = Path.Combine(vaultRoot, "keys");
Directory.CreateDirectory(keyRingDir);

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingDir))
    .SetApplicationName("Stampd.ByoCertDemo");

// -- Blazor + uploads --------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddCircuitOptions(o =>
    {
        // 10-min disconnect grace so a demo doesn't time out mid-explanation.
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    });

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 25 * 1024 * 1024; // 25 MB total form size
});

// -- Stampd services ---------------------------------------------------------
builder.Services.AddSingleton<CertVaultService>();
builder.Services.AddScoped<PdfSignerService>();

// Two TSA providers wired up, then composed into a failover chain. Order:
//   primary  → FreeTSA (free, community-run)
//   fallback → DigiCert (free, commercial uptime; engages when FreeTSA hangs/refuses)
// The failover chain itself is what PdfSignerService now consumes; the underlying
// providers are still individually resolvable for direct use if needed.
builder.Services.AddHttpClient<Stampd.Timestamp.FreeTsa.FreeTsaTimestampAuthorityProvider>(c =>
{
    // Tight 10-second timeout so FreeTSA's frequent hangs trip the failover quickly
    // instead of stalling the demo for 30 seconds.
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("digicert-tsa", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<Stampd.Core.Sealing.ITimestampAuthorityProvider>(sp =>
{
    var freetsa = sp.GetRequiredService<Stampd.Timestamp.FreeTsa.FreeTsaTimestampAuthorityProvider>();
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var digicertClient = httpFactory.CreateClient("digicert-tsa");
    var digicert = Stampd.Timestamp.Rfc3161.Rfc3161ServiceCollectionExtensions
        .CreateDigiCertProvider(digicertClient);
    // Pass the logger so per-attempt failures land in the rolling demo.log file
    // configured below. Adopters running their own DI host get the same flow
    // by passing their own ILogger.
    var failoverLogger = sp.GetRequiredService<
        Microsoft.Extensions.Logging.ILogger<
            Stampd.Timestamp.Rfc3161.FailoverTimestampAuthorityProvider>>();
    return new Stampd.Timestamp.Rfc3161.FailoverTimestampAuthorityProvider(
        new Stampd.Core.Sealing.ITimestampAuthorityProvider[] { freetsa, digicert },
        failoverLogger);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
