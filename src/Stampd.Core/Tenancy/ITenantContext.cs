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
