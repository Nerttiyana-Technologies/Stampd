using Stampd.Core;
using Stampd.Core.Entities;

namespace Stampd.WebApi.Services;

/// <summary>
/// Process-wide PAdES defaults derived from configuration. Wired into endpoint handlers so
/// every <see cref="SealingOptions"/> the WebApi produces has the configured target level
/// (B-B / B-T / B-LT / B-LTA) without each endpoint reading config independently.
/// </summary>
/// <param name="TargetLevel">The target PAdES conformance level for new signing requests.</param>
public sealed record PadesDefaults(PAdESLevel TargetLevel)
{
    /// <summary>
    /// Builds a fresh <see cref="SealingOptions"/> pre-populated with the configured target
    /// level. Endpoints that need different options for a specific request can use the
    /// <c>with</c> expression to override.
    /// </summary>
    public SealingOptions BuildSealingOptions() => new() { TargetLevel = TargetLevel };
}
