namespace Stampd.Core.Entities;

/// <summary>
/// v3.0 alpha.1 — per-tenant Admin assignment. Replaces v2.x's "any caller with the
/// Admin role claim is tenant-wide admin" model with explicit per-tenant grants, so
/// MSP-style deployments can scope a customer admin to only their tenant.
/// </summary>
/// <remarks>
/// <para>
/// Append-only by intent: an admin assignment is never hard-deleted; the
/// <see cref="RevokedAtUtc"/> field flips from null to a timestamp on revocation, and
/// the row stays for audit. "Who had admin on tenant X on date Y" remains answerable
/// long after the assignment ends.
/// </para>
/// <para>
/// AdminScope rows are NOT tenant-filtered in <c>Stampd.Infrastructure.StampdDbContext</c>'s
/// global query filters — they span tenants by design (one user can hold admin on
/// multiple tenants, and the auth handler needs to read them across the tenant boundary
/// to decide whether the current request's tenant is in the user's scope set).
/// </para>
/// </remarks>
public sealed class AdminScope
{
    public Guid Id { get; set; }

    /// <summary>
    /// The user receiving the admin grant. Matches the JWT <c>sub</c> claim and
    /// <see cref="Stampd.Core.Authorization.ICurrentActorContext.UserId"/> — opaque
    /// to Stampd, set by the IdP.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The tenant this scope grants admin on.</summary>
    public Guid TenantId { get; set; }

    public DateTimeOffset GrantedAtUtc { get; set; }

    /// <summary>The user who granted this scope (audit attribution).</summary>
    public string GrantedByUserId { get; set; } = string.Empty;

    /// <summary>Null while the scope is active; flipped to a timestamp on revocation.</summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string? RevokedByUserId { get; set; }

    /// <summary>Free-text reason captured at revoke time. Optional.</summary>
    public string? RevocationReason { get; set; }

    public Guid ConcurrencyToken { get; set; }

    /// <summary>True iff the scope is currently active (not revoked).</summary>
    public bool IsActive => RevokedAtUtc is null;
}
