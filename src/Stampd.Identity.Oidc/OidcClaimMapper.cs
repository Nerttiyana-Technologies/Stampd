using System.Security.Claims;

namespace Stampd.Identity.Oidc;

/// <summary>
/// v3.0 alpha.2 — pure function that translates an external IdP's role/group
/// claims into Stampd's role taxonomy (Admin / Sender / ReadOnly). Kept
/// separate from the JwtBearer wiring so it's trivially unit-testable.
/// </summary>
public static class OidcClaimMapper
{
    /// <summary>
    /// Pull every value of the configured <paramref name="roleClaim"/> off the
    /// principal and translate via <paramref name="mappings"/>. Returns each
    /// distinct Stampd role exactly once.
    /// </summary>
    /// <remarks>
    /// External groups not present in the mapping are SILENTLY dropped — adopters
    /// must be explicit about which group → role pairings are allowed. A user
    /// whose only group is unmapped ends up with zero Stampd roles (i.e. they
    /// can authenticate but can't pass any role-gated policy).
    /// </remarks>
    public static IReadOnlyCollection<string> MapRoles(
        ClaimsPrincipal principal,
        string roleClaim,
        IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleClaim);
        ArgumentNullException.ThrowIfNull(mappings);

        var rawValues = principal.FindAll(roleClaim).Select(c => c.Value);
        var stampdRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawValues)
        {
            if (mappings.TryGetValue(raw, out var stampdRole))
            {
                stampdRoles.Add(stampdRole);
            }
        }
        return stampdRoles;
    }

    /// <summary>
    /// Extract the tenant id from the configured <paramref name="tenantClaim"/>
    /// on the principal. Returns null if the claim is absent or not a parseable
    /// GUID. Doesn't throw — the auth handler decides whether missing-tenant is
    /// a hard failure (via <c>OidcRelayOptions.RequireTenantClaim</c>).
    /// </summary>
    public static Guid? TryGetTenantId(ClaimsPrincipal principal, string tenantClaim)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantClaim);

        var raw = principal.FindFirstValue(tenantClaim);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }
}
