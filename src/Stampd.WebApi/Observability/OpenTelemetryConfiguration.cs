using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Stampd.WebApi.Observability;

internal static class OpenTelemetryConfiguration
{
    public const string ServiceName = "Stampd.WebApi";

    /// <summary>
    /// Wires OpenTelemetry tracing + metrics. Default exporter is Console (visible in dev
    /// logs alongside Serilog output). Set <c>Stampd:Otel:OtlpEndpoint</c> to push to a
    /// real backend (Jaeger, Tempo, Datadog OTLP endpoint, Grafana Cloud, etc.).
    /// </summary>
    public static IServiceCollection AddStampdOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var otlpEndpoint = configuration["Stampd:Otel:OtlpEndpoint"];
        var serviceVersion = typeof(OpenTelemetryConfiguration).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ServiceName, serviceVersion: serviceVersion)
                .AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment",
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"),
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // Drop health-check noise from traces.
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
                else
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }
}
