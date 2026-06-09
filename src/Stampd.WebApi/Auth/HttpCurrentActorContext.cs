using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

using Stampd.Core.Authorization;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Auth;

/// <summary>
/// v2.0 Slice D — <see cref="ICurrentActorContext"/> implementation that reads from the
/// authenticated <see cref="ClaimsPrincipal"/> on the active HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// Scoped lifetime because <see cref="IHttpContextAccessor"/> is per-request and the
/// underlying ClaimsPrincipal is mutated by ASP.NET Core's auth middleware mid-pipeline
/// — we want a fresh read on every resolve.
/// </para>
/// <para>
/// Falls back gracefully when there's no HttpContext (background workers, hosted
/// services, integration tests that bypass the pipeline). All four members return safe
/// defaults (<c>null</c>, <c>false</c>) so callers don't need to null-check the context
/// itself.
/// </para>
/// </remarks>
internal sealed class HttpCurrentActorContext : ICurrentActorContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly StampdDbContext _db;
    private readonly IReadOnlyCollection<string> _superAdminUserIds;
    private IReadOnlyCollection<Guid>? _scopedTenantsCache;

    public HttpCurrentActorContext(
        IHttpContextAccessor httpContextAccessor,
        StampdDbContext db,
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        _httpContextAccessor = httpContextAccessor;
        _db = db;

        // v3.0 alpha.1 — bootstrap mechanism for the first admin assignment.
        // Without this, a fresh install has no admin scopes and no way to grant
        // the first one (the grant endpoint itself requires admin). Adopters set
        // Stampd:Auth:SuperAdminUserIds to a comma-separated list of UserIds that
        // bypass per-tenant scope checks. Treat as a permanent break-glass — do
        // NOT use for routine admin work in production.
        var raw = config["Stampd:Auth:SuperAdminUserIds"];
        _superAdminUserIds = string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    /// <summary>True iff the current user's id is in the super-admin override list.</summary>
    private bool IsSuperAdmin =>
        UserId is { } id && _superAdminUserIds.Contains(id, StringComparer.Ordinal);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? UserId
    {
        get
        {
            var principal = Principal;
            if (principal is null) return null;

            // JWT bearer middleware maps the "sub" claim to ClaimTypes.NameIdentifier
            // unless MapInboundClaims is false. We check both to survive either
            // configuration without forcing the WebApi to call
            // JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear() globally.
            return principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        }
    }

    public string? ActiveRole
    {
        get
        {
            var principal = Principal;
            if (principal is null) return null;

            // Pick the most-privileged role the caller holds. Admin > Sender > ReadOnly.
            // A caller carrying multiple role claims should be reported as the highest
            // tier — that's the role they're effectively acting in when they hit an
            // admin-gated endpoint.
            if (principal.IsInRole(StampdRoles.Admin)) return StampdRoles.Admin;
            if (principal.IsInRole(StampdRoles.Sender)) return StampdRoles.Sender;
            if (principal.IsInRole(StampdRoles.ReadOnly)) return StampdRoles.ReadOnly;
            return null;
        }
    }

    public bool IsAdmin => string.Equals(ActiveRole, StampdRoles.Admin, StringComparison.Ordinal);

    /// <summary>
    /// v3.0 alpha.1 — looks up active (non-revoked) admin scopes for the current
    /// user. Result is cached on the instance so repeated reads within one request
    /// don't hit the DB twice. Super-admins (configured via
    /// <c>Stampd:Auth:SuperAdminUserIds</c>) return a sentinel value the auth handler
    /// understands as "all tenants" — we expose this as a non-empty collection
    /// containing <see cref="Guid.Empty"/> so the handler can shortcut.
    /// </summary>
    public async ValueTask<IReadOnlyCollection<Guid>> GetAdminScopedTenantIdsAsync(CancellationToken ct)
    {
        if (_scopedTenantsCache is not null) return _scopedTenantsCache;

        if (UserId is not { } userId)
        {
            return _scopedTenantsCache = Array.Empty<Guid>();
        }

        // Super-admin override: return a single Guid.Empty as the "any tenant" marker.
        // The AdminScopeAuthorizationHandler unwraps this — it's specifically NOT a
        // valid TenantId so adopters can't accidentally collide with a real tenant.
        if (IsSuperAdmin)
        {
            return _scopedTenantsCache = new[] { Guid.Empty };
        }

        // AdminScopes is cross-tenant (no global query filter), so this read sees
        // every assignment for the user regardless of their current tenant context.
        var tenants = await _db.AdminScopes
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .Select(s => s.TenantId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return _scopedTenantsCache = tenants;
    }
}
