namespace Stampd.Core.Authorization;

/// <summary>
/// v2.0 — canonical role names used by both the WebApi authorization policies and the
/// UI's claim checks. Single source of truth so the role string can't drift between
/// the API (which emits the claim) and the UI (which reads it).
/// </summary>
/// <remarks>
/// <para>
/// v1.x had only authenticated / anonymous; v2.0 introduces three role tiers. Each role
/// is documented with its intent so adopters can choose the right policy when they add
/// new endpoints. Roles compose downwards: every endpoint <c>Admin</c> can hit, <c>Sender</c>
/// can hit on their own tenanted resources, and every endpoint <c>Sender</c> can hit a
/// <c>ReadOnly</c> can also hit in view-only form.
/// </para>
/// <para>
/// JWT claim mapping: each role appears as a separate <see cref="System.Security.Claims.ClaimTypes.Role"/>
/// claim on the bearer token. A user holding multiple roles (e.g. an admin who also sends
/// requests directly) carries multiple role claims.
/// </para>
/// </remarks>
public static class StampdRoles
{
    /// <summary>
    /// Tenant-level superuser. Can hit the <c>/api/admin/*</c> dashboard surface, run
    /// bulk operations (Slice D), regenerate recipient access tokens, void requests
    /// they didn't create, and view the cross-sender analytics. Admin role implies
    /// Sender + ReadOnly permissions on every endpoint.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Standard signing-workflow user. Can create templates, dispatch signing requests,
    /// view requests they created, download sealed PDFs from their own workflows. This
    /// is the role v1.x's "authenticated user" effectively had.
    /// </summary>
    public const string Sender = "Sender";

    /// <summary>
    /// View-only access to the management surface. Can list and read templates and
    /// signing requests but cannot create, modify, or delete anything. Intended for
    /// auditors, support staff, and read-only dashboard integrations.
    /// </summary>
    public const string ReadOnly = "ReadOnly";
}
