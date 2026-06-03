using System.Globalization;

using Microsoft.FluentUI.AspNetCore.Components;

using Serilog;

using Stampd.UI.Components;
using Stampd.UI.Services;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();
builder.Host.UseSerilog();

// Razor Components with both Static SSR and Interactive Server. Pages opt into
// InteractiveServer via @rendermode InteractiveServer; everything else stays static.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddFluentUIComponents();

// HttpClient pointed at the Stampd WebApi. The recipient signing endpoints are anonymous
// (per-token auth in the URL), so this client carries no Bearer token by default. Pages
// that talk to authenticated endpoints (designer → /api/templates) will need a separate
// authenticated client wired in the next session.
var apiBaseUrl = builder.Configuration["Stampd:Api:BaseUrl"] ?? "http://localhost:5070";
builder.Services.AddHttpClient<StampdApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddHttpClient<DesignerApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));

var app = builder.Build();

app.UseSerilogRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
