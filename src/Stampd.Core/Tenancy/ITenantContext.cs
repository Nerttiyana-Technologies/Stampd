namespace Stampd.Core.Tenancy;

/// <summary>
/// Resolves the current tenant for the executing request, background job, or unit of work.
/// Implementations typically read from the authenticated principal (Azure Entra ID claim,
/// custom JWT, etc.) and surface a single <see cref="Guid"/> the DbContext uses to scope
/// every query.
/// </summary>
/// <remarks>
/// A tenant identifier of <see cref="Guid.Empty"/> is reserved and means "no tenant in
/// scope" — used by background maintenance jobs that intentionally read across tenants.
/// The DbContext disables its query filter when <see cref="TenantId"/> is empty.
/// </remarks>
public interface ITenantContext
{
    /// <summary>The tenant the current operation runs under, or <see cref="Guid.Empty"/> if cross-tenant.</summary>
    Guid TenantId { get; }
}

/// <summary>
/// A trivial ambient implementation suitable for tests, background jobs, and the engine
/// spike. Production hosts should swap this out for one that reads from the request's
/// authenticated principal.
/// </summary>
public sealed class AmbientTenantContext : ITenantContext
{
    public AmbientTenantContext(Guid tenantId) => TenantId = tenantId;

    public Guid TenantId { get; }
}

/// <summary>
/// Ambient async-flow override for the current tenant. Background workers and tests use
/// this to switch the active tenant across an <c>async</c> boundary without rebuilding
/// the DI scope. Implementations of <see cref="ITenantContext"/> that opt in (e.g.
/// <c>HttpTenantContext</c>) consult <see cref="Current"/> first and fall back to their
/// usual resolution path when no scope is active.
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<Guid?> _current = new();

    /// <summary>The currently scoped tenant, or null when no scope is active.</summary>
    public static Guid? Current => _current.Value;

    /// <summary>
    /// Pushes <paramref name="tenantId"/> as the ambient tenant for the duration of the
    /// returned scope. Dispose to restore the previous value. Use with <c>using</c>:
    /// <code>
    /// using (TenantScope.Enter(jobTenantId)) { await workflow.DispatchAsync(...); }
    /// </code>
    /// </summary>
    public static IDisposable Enter(Guid tenantId)
    {
        var prior = _current.Value;
        _current.Value = tenantId;
        return new Reset(prior);
    }

    private sealed class Reset : IDisposable
    {
        private readonly Guid? _prior;

        public Reset(Guid? prior) => _prior = prior;

        public void Dispose() => _current.Value = _prior;
    }
}
