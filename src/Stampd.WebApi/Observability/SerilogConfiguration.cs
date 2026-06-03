using System.Globalization;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Stampd.WebApi.Observability;

internal static class SerilogConfiguration
{
    /// <summary>
    /// Bootstraps Serilog as the host's only logging provider. Console gets human-readable
    /// output for dev; file gets compact JSON (one JSON object per line) for grep + tail
    /// pipelines and machine ingestion.
    /// </summary>
    public static void ConfigureSerilog(this WebApplicationBuilder builder)
    {
        var logDirectory = builder.Configuration["Stampd:Logging:Directory"]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".stampd",
                "logs");

        Directory.CreateDirectory(logDirectory);

        builder.Host.UseSerilog((context, _, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("Application", "Stampd.WebApi")
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .WriteTo.Console(
                    outputTemplate:
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj} {Properties:j}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture)
                .WriteTo.File(
                    formatter: new CompactJsonFormatter(),
                    path: Path.Combine(logDirectory, "stampd-.json"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 50 * 1024 * 1024);
        });
    }
}
