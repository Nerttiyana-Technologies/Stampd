using System.Reflection;

namespace Stampd.WebApi.Endpoints;

internal static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder builder)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "dev";

        builder.MapGet("/health", () => new
        {
            status = "healthy",
            service = "Stampd.WebApi",
            version,
            timestamp = DateTimeOffset.UtcNow,
        })
        .WithName("Health")
        .WithSummary("Liveness probe — confirms the API process is up and responding.");

        return builder;
    }
}
