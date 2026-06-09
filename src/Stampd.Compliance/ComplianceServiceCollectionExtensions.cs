using Microsoft.Extensions.DependencyInjection;

namespace Stampd.Compliance;

/// <summary>
/// v3.0.0 — DI registration helper for the compliance gate. Adopters who want
/// compliance bundle enforcement add <c>services.AddStampdCompliance()</c> in
/// their composition root.
/// </summary>
public static class ComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddStampdCompliance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IComplianceGate, ComplianceGate>();
        return services;
    }
}
