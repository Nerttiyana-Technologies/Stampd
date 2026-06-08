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
builder.Services.AddHttpClient<Stampd.Timestamp.FreeTsa.FreeTsaTimestampAuthorityProvider>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
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
