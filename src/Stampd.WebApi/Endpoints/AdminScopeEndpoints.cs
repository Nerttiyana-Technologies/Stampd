using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Authorization;
using Stampd.Core.Entities;
using Stampd.Core.Tenancy;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>
/// v3.0 alpha.1 — CRUD endpoints for per-tenant admin assignments. All three live
/// under the <c>Admin</c> policy (the new tenant-scoped one), so an existing admin
/// on tenant X can grant / revoke / list assignments only for tenant X.
/// </summary>
/// <remarks>
/// Cross-tenant operations (e.g. moving an admin from tenant A to tenant B) require
/// the super-admin override configured via <c>Stampd:Auth:SuperAdminUserIds</c>, since
/// the auth handler treats super-admin as "any tenant". This is intentional: tenant
/// isolation is a security boundary, not just an organization concept.
/// </remarks>
internal static class AdminScopeEndpoints
{
    public static IEndpointRouteBuilder MapAdminScopes(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/scopes").WithTags("AdminScopes");

        group.MapPost("/", GrantAsync)
            .WithName("GrantAdminScope")
            .WithSummary("Grant an admin assignment to a user on the current tenant. v3.0 alpha.1.");

        group.MapDelete("/{id:guid}", RevokeAsync)
            .WithName("RevokeAdminScope")
            .WithSummary("Revoke an admin assignment (soft-delete). v3.0 alpha.1.");

        group.MapGet("/", ListAsync)
            .WithName("ListAdminScopes")
            .WithSummary("List admin assignments on the current tenant. v3.0 alpha.1.");

        return builder;
    }

    /// <summary>Request body for grant.</summary>
    public sealed record GrantBody(string UserId);

    /// <summary>Request body for revoke.</summary>
    public sealed record RevokeBody(string? Reason);

    /// <summary>
    /// Grant admin scope. Tenant comes from the current request context — admins
    /// can only grant on the tenant they're currently scoped to. UserId is the IdP
    /// subject identifier; we don't validate it against an external user store
    /// (Stampd doesn't have one), so adopters must ensure UserId matches what
    /// their IdP will issue as the JWT <c>sub</c> claim.
    /// </summary>
    private static async Task<IResult> GrantAsync(
        [FromBody] GrantBody body,
        [FromServices] StampdDbContext db,
        [FromServices] ITenantContext tenant,
        [FromServices] ICurrentActorContext actor,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        {
            return Results.BadRequest(new { error = "UserId is required." });
        }
        if (tenant.TenantId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "No tenant in scope; grant must run inside a tenant context." });
        }
        if (actor.UserId is null)
        {
            return Results.Unauthorized();
        }

        var userId = body.UserId.Trim();

        // Idempotency: if an active scope already exists for (user, tenant), return
        // it instead of creating a duplicate. Re-granting an already-revoked scope
        // creates a new row (we want the audit trail to capture the gap).
        var existing = await db.AdminScopes
            .Where(s => s.UserId == userId
                && s.TenantId == tenant.TenantId
                && s.RevokedAtUtc == null)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Results.Ok(ToResponse(existing));
        }

        var scope = new AdminScope
        {
            UserId = userId,
            TenantId = tenant.TenantId,
            GrantedByUserId = actor.UserId,
        };
        db.AdminScopes.Add(scope);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Created($"/api/admin/scopes/{scope.Id}", ToResponse(scope));
    }

    /// <summary>
    /// Revoke an admin scope (soft-delete via RevokedAtUtc). The row stays on the
    /// audit trail; only the IsActive flag flips. Revoking your own scope is
    /// permitted — break-glass scenarios sometimes call for it — but the WebApi
    /// will reject your next admin request as a consequence.
    /// </summary>
    private static async Task<IResult> RevokeAsync(
        Guid id,
        [FromBody] RevokeBody? body,
        [FromServices] StampdDbContext db,
        [FromServices] ITenantContext tenant,
        [FromServices] ICurrentActorContext actor,
        CancellationToken ct)
    {
        if (actor.UserId is null)
        {
            return Results.Unauthorized();
        }

        var scope = await db.AdminScopes
            .Where(s => s.Id == id && s.TenantId == tenant.TenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (scope is null)
        {
            return Results.NotFound();
        }
        if (scope.RevokedAtUtc is not null)
        {
            // Already revoked — idempotent: return the existing row instead of erroring.
            return Results.Ok(ToResponse(scope));
        }

        scope.RevokedAtUtc = DateTimeOffset.UtcNow;
        scope.RevokedByUserId = actor.UserId;
        scope.RevocationReason = body?.Reason?.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(ToResponse(scope));
    }

    /// <summary>
    /// List admin scopes for the current tenant. Returns both active and revoked
    /// rows ordered by GrantedAtUtc desc so the UI shows the audit trail without
    /// extra calls. Filterable to active-only via the <c>activeOnly</c> query param.
    /// </summary>
    private static async Task<IResult> ListAsync(
        [FromQuery] bool? activeOnly,
        [FromServices] StampdDbContext db,
        [FromServices] ITenantContext tenant,
        CancellationToken ct)
    {
        var query = db.AdminScopes
            .Where(s => s.TenantId == tenant.TenantId);

        if (activeOnly == true)
        {
            query = query.Where(s => s.RevokedAtUtc == null);
        }

        var items = await query
            .OrderByDescending(s => s.GrantedAtUtc)
            .Select(s => new
            {
                id = s.Id,
                userId = s.UserId,
                tenantId = s.TenantId,
                grantedAtUtc = s.GrantedAtUtc,
                grantedByUserId = s.GrantedByUserId,
                revokedAtUtc = s.RevokedAtUtc,
                revokedByUserId = s.RevokedByUserId,
                revocationReason = s.RevocationReason,
                isActive = s.RevokedAtUtc == null,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new { items });
    }

    /// <summary>Shared projection for grant + revoke responses.</summary>
    private static object ToResponse(AdminScope scope) => new
    {
        id = scope.Id,
        userId = scope.UserId,
        tenantId = scope.TenantId,
        grantedAtUtc = scope.GrantedAtUtc,
        grantedByUserId = scope.GrantedByUserId,
        revokedAtUtc = scope.RevokedAtUtc,
        revokedByUserId = scope.RevokedByUserId,
        revocationReason = scope.RevocationReason,
        isActive = scope.IsActive,
    };
}
