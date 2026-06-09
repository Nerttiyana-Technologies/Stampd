namespace Stampd.Core.Authorization;

/// <summary>
/// v2.0 Slice D — server-side abstraction for "who is the authenticated caller of the
/// current request, and what role are they wearing." Used by services (especially
/// <c>SigningWorkflowService</c>) to attribute audit events to a real actor instead of
/// leaving <c>AuditEvent.ActorUserId</c> null on admin operations.
/// </summary>
/// <remarks>
/// <para>
/// The WebApi-side implementation reads <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// off the active HttpContext. In test fixtures or background workers that don't have an
/// HttpContext, a null or anonymous actor is the correct value — those code paths emit
/// audit events with <c>ActorUserId = null</c>, which v2.0 explicitly allows (pre-v2 rows
/// are also null, so verifiers must already tolerate the absence).
/// </para>
/// <para>
/// Two flavors of "who":
/// </para>
/// <list type="bullet">
///   <item>
///     <see cref="UserId"/> — the JWT <c>sub</c> claim, opaque identifier (typically email
///     or a UUID depending on the IdP). Stored verbatim on the audit row.
///   </item>
///   <item>
///     <see cref="ActiveRole"/> — the single role the caller is acting in. Multi-role
///     callers (e.g. an admin who's also a sender) pick one per request; the WebApi
///     defaults to the most-privileged role they hold.
///   </item>
/// </list>
/// </remarks>
public interface ICurrentActorContext
{
    /// <summary>
    /// True iff a JWT was validated for the current request and the principal has at
    /// least one identity. Background workers and unauthenticated public endpoints
    /// (notably the recipient signing flow) return false.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The JWT <c>sub</c> claim value, or null when no JWT is present. Stored on
    /// <c>AuditEvent.ActorUserId</c> for admin operations so audit consumers can trace
    /// "who voided this signing request."
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// The most-privileged role the active user holds (Admin > Sender > ReadOnly), or
    /// null when no role claims are present. Stored on <c>AuditEvent.ActorRole</c> so
    /// audit consumers can filter "all admin actions across the tenant" without
    /// joining to a separate user table.
    /// </summary>
    string? ActiveRole { get; }

    /// <summary>
    /// Convenience: true iff <see cref="ActiveRole"/> equals <see cref="StampdRoles.Admin"/>.
    /// Avoids string comparison at every call-site.
    /// </summary>
    bool IsAdmin { get; }

    /// <summary>
    /// v3.0 alpha.1 — the set of TenantIds where the current user holds an active
    /// (non-revoked) Admin scope assignment. Empty set for users with no admin
    /// grants; one-element set for typical single-tenant deployments. The auth
    /// handler reads this to decide whether the current request's tenant is in the
    /// scope set.
    /// </summary>
    /// <remarks>
    /// Async because the implementation reads from the AdminScopes table. The
    /// WebApi impl caches the result per-request so repeated calls in one request
    /// don't re-query. Returns an empty collection (not null) when there's no
    /// authenticated user.
    /// </remarks>
    ValueTask<IReadOnlyCollection<Guid>> GetAdminScopedTenantIdsAsync(CancellationToken ct);
}
