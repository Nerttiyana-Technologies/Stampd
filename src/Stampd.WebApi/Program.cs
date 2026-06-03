using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Scalar.AspNetCore;

using Serilog;

using Stampd.Core;
using Stampd.Core.Revocation;
using Stampd.Core.Sealing;
using Stampd.Core.Tenancy;
using Stampd.Crypto.LocalCertificate;
using Stampd.Crypto.Vault;
using Stampd.Engine;
using Stampd.Engine.Rendering;
using Stampd.Infrastructure;
using Stampd.Infrastructure.Sqlite;
using Stampd.Revocation.Http;
using Stampd.Storage.FileSystem;
using Stampd.Timestamp.FreeTsa;
using Stampd.Timestamp.Rfc3161;
using Stampd.WebApi.Auth;
using Stampd.WebApi.Endpoints;
using Stampd.WebApi.Observability;
using Stampd.WebApi.Observability.HealthChecks;
using Stampd.WebApi.Services;
using Stampd.WebApi.Tenancy;

// PdfSharp 6.x has no default font resolver outside Windows — register at process start.
PlatformFontResolver.Register();

var builder = WebApplication.CreateBuilder(args);

// ---- Observability: Serilog FIRST so config loading is captured ----
builder.ConfigureSerilog();

// OpenTelemetry traces + metrics
builder.Services.AddStampdOpenTelemetry(builder.Configuration);

// ---- Configuration ----
var certPath = builder.Configuration["Stampd:SigningCertificate:Pkcs12Path"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "spike-signer.pfx");
var certPassword = builder.Configuration["Stampd:SigningCertificate:Password"] ?? "stampd-spike";
var enableTsa = builder.Configuration.GetValue("Stampd:Tsa:Enabled", defaultValue: true);
var tsaEndpoint = builder.Configuration["Stampd:Tsa:Endpoint"];
var storageRoot = builder.Configuration["Stampd:Storage:FileSystemRoot"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "documents");
var sqlitePath = builder.Configuration["Stampd:Database:SqlitePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stampd", "stampd.db");

Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
Directory.CreateDirectory(storageRoot);

// ---- Services ----
builder.Services.AddOpenApi();

// Persistence
builder.Services.AddStampdSqlite($"Data Source={sqlitePath}");

// Document storage
builder.Services.AddFileSystemDocumentStorage(storageRoot);

// Tenant context (header-based)
var defaultTenantId = Guid.Parse(
    builder.Configuration["Stampd:Tenancy:DefaultTenantId"] ?? "00000000-0000-0000-0000-000000000001");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext>(sp =>
    new HttpTenantContext(sp.GetRequiredService<IHttpContextAccessor>(), defaultTenantId));

// Sealing: LocalCertificate or Vault by config.
var sealingProvider = builder.Configuration["Stampd:Sealing:Provider"] ?? "Local";
if (string.Equals(sealingProvider, "Vault", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddVaultSealing(options =>
    {
        options.VaultAddress = new Uri(builder.Configuration["Stampd:Sealing:Vault:Address"]
            ?? throw new InvalidOperationException("Vault:Address required"));
        options.Token = builder.Configuration["Stampd:Sealing:Vault:Token"];
        options.TransitMountPath = builder.Configuration["Stampd:Sealing:Vault:TransitMountPath"] ?? "transit";
        options.KeyName = builder.Configuration["Stampd:Sealing:Vault:KeyName"];
        options.CertificatePath = builder.Configuration["Stampd:Sealing:Vault:CertificatePath"];
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

if (enableTsa)
{
    // TSA provider selection:
    //   - "FreeTSA" (default) → free public TSA, dev / demo / low-volume.
    //   - "Rfc3161"           → generic provider, configurable URL + optional basic auth +
    //                           optional mTLS client cert. Use for DigiCert / GlobalSign /
    //                           Sectigo / internal Microsoft AD CS / EJBCA TSAs.
    var tsaProvider = builder.Configuration["Stampd:Tsa:Provider"] ?? "FreeTSA";
    if (string.Equals(tsaProvider, "Rfc3161", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddRfc3161TimestampAuthority(options =>
        {
            options.Name = builder.Configuration["Stampd:Tsa:Name"] ?? "RFC3161";
            options.Endpoint = new Uri(builder.Configuration["Stampd:Tsa:Endpoint"]
                ?? throw new InvalidOperationException(
                    "Stampd:Tsa:Endpoint is required when Stampd:Tsa:Provider = 'Rfc3161'."));
            options.BasicAuthUsername = builder.Configuration["Stampd:Tsa:BasicAuthUsername"];
            options.BasicAuthPassword = builder.Configuration["Stampd:Tsa:BasicAuthPassword"];
            options.ClientCertificatePkcs12Path = builder.Configuration["Stampd:Tsa:ClientCertificatePkcs12Path"];
            options.ClientCertificatePassword = builder.Configuration["Stampd:Tsa:ClientCertificatePassword"];
            options.RequestedPolicyOid = builder.Configuration["Stampd:Tsa:RequestedPolicyOid"];
            options.RequestTsaCertificate = builder.Configuration.GetValue(
                "Stampd:Tsa:RequestTsaCertificate", defaultValue: true);
            options.IncludeNonce = builder.Configuration.GetValue(
                "Stampd:Tsa:IncludeNonce", defaultValue: true);
        });
    }
    else
    {
        builder.Services.AddFreeTsaTimestampAuthority(tsaEndpoint is null ? null : new Uri(tsaEndpoint));
    }
}

// Revocation providers for PAdES B-LT. When enabled, the engine pre-fetches OCSP / CRL
// material for each cert in the signer chain and embeds it in a /DSS catalog entry.
// Adopters who don't need B-LT can leave this disabled — engine output stays at B-T.
var enableRevocation = builder.Configuration.GetValue("Stampd:Revocation:Enabled", defaultValue: false);
if (enableRevocation)
{
    builder.Services.AddHttpRevocationProviders();
}

// Process-wide PAdES level. Endpoints read this via PadesDefaults so callers don't have to
// pass it in every request body. Adopters who want B-LT set Stampd:Pades:TargetLevel="BLT"
// and ensure Stampd:Revocation:Enabled=true.
var padesLevel = Enum.TryParse<Stampd.Core.Entities.PAdESLevel>(
    builder.Configuration["Stampd:Pades:TargetLevel"], ignoreCase: true, out var parsedLevel)
    ? parsedLevel
    : Stampd.Core.Entities.PAdESLevel.BT;
builder.Services.AddSingleton(new Stampd.WebApi.Services.PadesDefaults(padesLevel));

builder.Services.AddSingleton<IStampdEngine>(sp =>
{
    var sealing = sp.GetRequiredService<ICryptographicSealingProvider>();
    var tsa = sp.GetService<ITimestampAuthorityProvider>();
    var revocation = sp.GetService<IRevocationProvider>();
    return new PdfSharpStampdEngine(sealing, tsa, revocation);
});

builder.Services.AddScoped<SigningWorkflowService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ---- Health checks ----
builder.Services.AddHealthChecks()
    .AddDbContextCheck<StampdDbContext>(
        name: "database",
        tags: ["live", "ready"])
    .AddCheck<SealingProviderHealthCheck>(
        name: "sealing-provider",
        tags: ["ready"])
    .AddCheck<TimestampAuthorityHealthCheck>(
        name: "timestamp-authority",
        failureStatus: HealthStatus.Degraded,
        tags: ["ready"]);

// ---- AuthN/AuthZ ----
//
// IMPORTANT: bind JwtOptions via the DI options pattern (NOT by reading
// builder.Configuration["..."] eagerly here). WebApplicationFactory adds its in-memory
// config overrides via ConfigureAppConfiguration which runs DURING builder.Build() —
// later than the top-level statements in Program.cs. If we read the JWT issuer/audience
// here, integration tests would get the appsettings.json defaults instead of their
// overrides, and every JWT would fail validation with 401.
//
// The fix: register JwtBearerOptions configuration as a DI callback that runs at
// resolve-time via IOptionsMonitor<JwtOptions>, so it sees the final, fully-merged config.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Stampd:Auth:Jwt"));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptionsMonitor<JwtOptions>>((bearerOptions, jwtMonitor) =>
    {
        var jwt = jwtMonitor.CurrentValue;
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
    });

builder.Services.AddAuthorization();

// ---- Rate limiting ----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sign", httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = 10,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

// ---- Request size caps (50 MB) ----
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
});

var app = builder.Build();

// ---- Apply migrations on startup (dev convenience) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

// ---- Pipeline ----
app.UseSerilogRequestLogging();
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Stampd API");
        options.WithTheme(ScalarTheme.BluePlanet);
    });
}

// HTTPS redirection in production is typically handled at the load balancer / ingress.
// In-process redirection in the Testing environment causes a 307 that the test HttpClient
// follows, stripping the Authorization header in the process — every protected-endpoint
// test then fails with 401. Skip the middleware when running under WebApplicationFactory.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---- Endpoints ----
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponseAsync,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckJsonResponseWriter.WriteResponseAsync,
}).AllowAnonymous();

// Endpoint extension methods return IEndpointRouteBuilder, which can't chain
// RequireAuthorization / AllowAnonymous / RequireRateLimiting directly. Wrap each
// extension call in a MapGroup that pre-applies the policy — the group's conventions
// propagate to every endpoint mapped inside it.

// Anonymous surface: health, dev token issuance, recipient signing (recipient is
// authenticated by the per-recipient access token in the URL, not by JWT).
var anonGroup = app.MapGroup("").AllowAnonymous();
anonGroup.MapHealth();
anonGroup.MapAuth();
anonGroup.MapRecipientSigning();

// Sign endpoints: rate-limited AND require auth.
var signGroup = app.MapGroup("")
    .RequireAuthorization()
    .RequireRateLimiting("sign");
signGroup.MapSign();
signGroup.MapBinarySign();

// Management endpoints: auth required, no rate limit (admin actions, not sign hot path).
var managementGroup = app.MapGroup("").RequireAuthorization();
managementGroup.MapTemplates();
managementGroup.MapSigningRequests();
managementGroup.MapSignedDocuments();

try
{
    Log.Information("Stampd.WebApi starting on {EnvName}", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Stampd.WebApi terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
