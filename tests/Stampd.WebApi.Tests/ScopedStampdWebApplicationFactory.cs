using Microsoft.AspNetCore.Hosting;

namespace Stampd.WebApi.Tests;

/// <summary>
/// v3.0 alpha.1 — <see cref="StampdWebApplicationFactory"/> subclass that accepts
/// per-instance config overrides via constructor. Used by tests that need a
/// distinct config value (e.g. <c>Stampd:Auth:SuperAdminUserIds</c>,
/// <c>Stampd:Auth:Mode</c>) without racing on process-wide environment variables.
/// </summary>
/// <remarks>
/// We can't add this ctor to the base class because xUnit's <c>IClassFixture&lt;T&gt;</c>
/// wiring requires the fixture type to have exactly ONE public ctor and resolves
/// it reflectively without honoring default parameter values. Subclassing keeps
/// the base parameterless ctor intact for v1.x / v2.x tests using IClassFixture,
/// and gives v3.0 tests an explicit knob.
///
/// v3.0 alpha.2 — accepts an optional environment override (default "Testing")
/// so the auth-mode startup guard's Production-vs-Development behavior can be
/// exercised.
/// </remarks>
public sealed class ScopedStampdWebApplicationFactory : StampdWebApplicationFactory
{
    private readonly IReadOnlyDictionary<string, string?> _extras;
    private readonly string _environment;

    public ScopedStampdWebApplicationFactory(
        IReadOnlyDictionary<string, string?> extras,
        string environment = "Testing")
    {
        ArgumentNullException.ThrowIfNull(extras);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        _extras = extras;
        _environment = environment;
    }

    protected override IReadOnlyDictionary<string, string?>? ExtraConfig() => _extras;

    protected override string EnvironmentName() => _environment;
}
