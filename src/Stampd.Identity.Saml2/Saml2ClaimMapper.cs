using System.Security.Claims;

namespace Stampd.Identity.Saml2;

/// <summary>
/// v3.0 alpha.3 — pure function that translates a SAML assertion's role /
/// attribute claims into Stampd's role taxonomy. Structurally identical to
/// <see cref="Stampd.Identity.Oidc.OidcClaimMapper"/>; kept separate so SAML2
/// adopters don't get an implicit OIDC dependency at the package level (the
/// project-level reference exists for the StampdAuthMode enum and is removed
/// when we promote both to v3.0.0 stable and ship StampdRoles + the shared
/// mapper from Stampd.Core).
/// </summary>
public static class Saml2ClaimMapper
{
    /// <summary>
    /// Pull every value of the configured <paramref name="roleClaim"/> off the
    /// principal and translate via <paramref name="mappings"/>. Returns each
    /// distinct Stampd role exactly once. External attributes not in the
    /// mapping are silently dropped — adopters must explicitly opt every
    /// IdP group into the role taxonomy.
    /// </summary>
    public static IReadOnlyCollection<string> MapRoles(
        ClaimsPrincipal principal,
        string roleClaim,
        IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleClaim);
        ArgumentNullException.ThrowIfNull(mappings);

        var stampdRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in principal.FindAll(roleClaim))
        {
            if (mappings.TryGetValue(claim.Value, out var stampdRole))
            {
                stampdRoles.Add(stampdRole);
            }
        }
        return stampdRoles;
    }

    /// <summary>
    /// Extract the tenant id from the configured <paramref name="tenantClaim"/>.
    /// Returns null when the claim is absent or unparseable as a GUID. The
    /// ACS handler decides whether missing tenant is a hard failure via
    /// <see cref="Saml2RelayOptions.RequireTenantClaim"/>.
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
