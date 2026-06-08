// Stampd.ByoCertDemo.WebApi — customer-integration reference (Blazor Server).
//
// This sample is the production-shaped counterpart of Stampd.ByoCertDemo. It
// signs PDFs by calling Stampd.WebApi over HTTP instead of hosting the engine
// in-process. The customer's developers will recognise this shape — they'll
// build their own client UI exactly like this, pointed at their deployment of
// Stampd.WebApi.
//
// Boots at http://localhost:5182.
//
// Prerequisites for running the demo:
//   1. Stampd.WebApi must be running. From the repo root:
//        dotnet run --project src/Stampd.WebApi
//      It listens on http://localhost:5070 by default.
//   2. That WebApi must be configured with a sealing provider (a cert) — the
//      LocalCertificate provider with a self-signed dev cert is the default
//      when ASPNETCORE_ENVIRONMENT=Development.
//
// The customer's takeaway: deploy Stampd.WebApi with your real signing cert,
// build a UI client like this one against the public API contract, done.

using Stampd.ByoCertDemo.WebApi.Components;
using Stampd.ByoCertDemo.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server — same UI shell as the standalone sample, lifted with no Stampd
// engine references.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddCircuitOptions(o =>
    {
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    });

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 25 * 1024 * 1024;
});

// HTTP client typed for Stampd.WebApi. Base address comes from config so the
// customer can point at any deployment (dev, staging, their internal hostname).
builder.Services.AddHttpClient<StampdWebApiClient>(c =>
{
    var baseUrl = builder.Configuration["Stampd:WebApi:BaseUrl"] ?? "http://localhost:5070";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(60); // bulk PDFs + TSA round-trip together
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
