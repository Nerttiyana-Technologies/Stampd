using Microsoft.EntityFrameworkCore;

using Scalar.AspNetCore;

using Stampd.Core;
using Stampd.Core.Sealing;
using Stampd.Core.Tenancy;
using Stampd.Crypto.LocalCertificate;
using Stampd.Crypto.Vault;
using Stampd.Engine;
using Stampd.Engine.Rendering;
using Stampd.Infrastructure;
using Stampd.Infrastructure.Sqlite;
using Stampd.Storage.FileSystem;
using Stampd.Timestamp.FreeTsa;
using Stampd.WebApi.Endpoints;
using Stampd.WebApi.Services;
using Stampd.WebApi.Tenancy;

// PdfSharp 6.x has no default font resolver outside Windows — register at process start.
PlatformFontResolver.Register();

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
var certPath = builder.Configuration["Stampd:SigningCertificate:Pkcs12Path"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".stampd",
        "spike-signer.pfx");

var certPassword = builder.Configuration["Stampd:SigningCertificate:Password"]
    ?? "stampd-spike";

var enableTsa = builder.Configuration.GetValue("Stampd:Tsa:Enabled", defaultValue: true);
var tsaEndpoint = builder.Configuration["Stampd:Tsa:Endpoint"];

var storageRoot = builder.Configuration["Stampd:Storage:FileSystemRoot"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".stampd",
        "documents");

var sqlitePath = builder.Configuration["Stampd:Database:SqlitePath"]
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".stampd",
        "stampd.db");

Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
Directory.CreateDirectory(storageRoot);

// ---- Services ----
builder.Services.AddOpenApi();

// Persistence
builder.Services.AddStampdSqlite($"Data Source={sqlitePath}");

// Document storage
builder.Services.AddFileSystemDocumentStorage(storageRoot);

// Header-based tenant context: reads X-Stampd-Tenant from the request and falls back to
// the configured default. Production should swap this for a claims-based resolver once
// AuthN/AuthZ is wired in.
var defaultTenantId = Guid.Parse(
    builder.Configuration["Stampd:Tenancy:DefaultTenantId"]
    ?? "00000000-0000-0000-0000-000000000001");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext>(sp =>
    new HttpTenantContext(sp.GetRequiredService<IHttpContextAccessor>(), defaultTenantId));

// Sealing — pick between LocalCertificate and Vault based on configuration.
var sealingProvider = builder.Configuration["Stampd:Sealing:Provider"] ?? "Local";

if (string.Equals(sealingProvider, "Vault", StringComparison.OrdinalIgnoreCase))
{
    var addressRaw = builder.Configuration["Stampd:Sealing:Vault:Address"]
        ?? throw new InvalidOperationException("Stampd:Sealing:Vault:Address is required when Provider=Vault.");
    var vaultToken = builder.Configuration["Stampd:Sealing:Vault:Token"]
        ?? throw new InvalidOperationException("Stampd:Sealing:Vault:Token is required when Provider=Vault.");
    var keyName = builder.Configuration["Stampd:Sealing:Vault:KeyName"]
        ?? throw new InvalidOperationException("Stampd:Sealing:Vault:KeyName is required when Provider=Vault.");
    var vaultCertPath = builder.Configuration["Stampd:Sealing:Vault:CertificatePath"]
        ?? throw new InvalidOperationException("Stampd:Sealing:Vault:CertificatePath is required when Provider=Vault.");
    var transitMount = builder.Configuration["Stampd:Sealing:Vault:TransitMountPath"] ?? "transit";

    builder.Services.AddVaultSealing(options =>
    {
        options.VaultAddress = new Uri(addressRaw);
        options.Token = vaultToken;
        options.TransitMountPath = transitMount;
        options.KeyName = keyName;
        options.CertificatePath = vaultCertPath;
    });
}
else
{
    builder.Services.AddLocalCertificateSealing(_ =>
    {
        var (cert, _) = SelfSignedCertificateFactory.CreateOrLoad(certPath, certPassword);
        return cert;
    });
}

// TSA (optional)
if (enableTsa)
{
    builder.Services.AddFreeTsaTimestampAuthority(
        tsaEndpoint is null ? null : new Uri(tsaEndpoint));
}

// Engine
builder.Services.AddSingleton<IStampdEngine>(sp =>
{
    var sealing = sp.GetRequiredService<ICryptographicSealingProvider>();
    var tsa = sp.GetService<ITimestampAuthorityProvider>();
    return new PdfSharpStampdEngine(sealing, tsa);
});

// Workflow service (orchestrates template + recipient state)
builder.Services.AddScoped<SigningWorkflowService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// ---- Apply migrations on startup (dev convenience) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

// ---- Pipeline ----
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Stampd API");
        options.WithTheme(ScalarTheme.BluePlanet);
    });
}

app.UseHttpsRedirection();

app.MapHealth();
app.MapSign();
app.MapBinarySign();
app.MapTemplates();
app.MapSigningRequests();
app.MapRecipientSigning();
app.MapSignedDocuments();

app.Run();
