using Microsoft.AspNetCore.Authorization;

using Stampd.Core.Authorization;
using Stampd.Core.Tenancy;

namespace Stampd.WebApi.Auth;

/// <summary>
/// v3.0 alpha.1 — replaces the v2.x `Admin` role-only requirement with a
/// per-tenant scope check. Caller must:
/// <list type="number">
///   <item>Carry the <see cref="StampdRoles.Admin"/> role claim (preserved from v2.x), AND</item>
///   <item>Hold an active AdminScope assignment for the request's tenant.</item>
/// </list>
/// Super-admins (configured via <c>Stampd:Auth:SuperAdminUserIds</c>) bypass step 2.
/// </summary>
/// <remarks>
/// Both gates are required so a stolen JWT alone isn't enough to act as admin on
/// an arbitrary tenant — the attacker would also need a matching AdminScope row,
/// which only an existing admin can grant via <c>POST /api/admin/scopes</c>.
/// </remarks>
internal sealed class AdminScopeRequirement : IAuthorizationRequirement
{
}

internal sealed class AdminScopeAuthorizationHandler : AuthorizationHandler<AdminScopeRequirement>
{
    private readonly ICurrentActorContext _actor;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AdminScopeAuthorizationHandler> _logger;

    public AdminScopeAuthorizationHandler(
        ICurrentActorContext actor,
        ITenantContext tenant,
        ILogger<AdminScopeAuthorizationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(logger);
        _actor = actor;
        _tenant = tenant;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminScopeRequirement requirement)
    {
        // Gate 1: must carry the Admin role claim. This preserves v2.x's wire format
        // for the role taxonomy — IdPs still emit the same claim, and downstream
        // role-aware code (the UI nav, the Designer.razor admin check) keeps working.
        if (!context.User.IsInRole(StampdRoles.Admin))
        {
            return; // implicit fail
        }

        // Gate 2: must hold an active AdminScope for the current request's tenant.
        var scoped = await _actor.GetAdminScopedTenantIdsAsync(CancellationToken.None).ConfigureAwait(false);

        // Super-admin marker: a single Guid.Empty entry means "any tenant", inserted
        // by HttpCurrentActorContext when the user is in Stampd:Auth:SuperAdminUserIds.
        // Adopters shouldn't be able to spoof this — a real AdminScope row will never
        // carry TenantId = Guid.Empty (the AdminScopeConfiguration marks it required
        // but doesn't prevent Empty; we rely on the grant endpoint to enforce non-empty).
        if (scoped.Count == 1 && scoped.First() == Guid.Empty)
        {
            _logger.LogInformation(
                "AdminScope check: super-admin override for user {UserId} on tenant {TenantId}",
                _actor.UserId, _tenant.TenantId);
            context.Succeed(requirement);
            return;
        }

        if (scoped.Contains(_tenant.TenantId))
        {
            context.Succeed(requirement);
            return;
        }

        _logger.LogWarning(
            "AdminScope check: user {UserId} carries Admin role but has no active scope for tenant {TenantId}; denying",
            _actor.UserId, _tenant.TenantId);
        // implicit fail: don't call context.Succeed, ASP.NET Core defaults to 403.
    }
}
