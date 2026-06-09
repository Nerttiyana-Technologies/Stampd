namespace Stampd.Identity.Oidc;

/// <summary>
/// v3.0 alpha.2 — config for the OIDC relay. Bound from <c>Stampd:Auth:Oidc</c>.
/// </summary>
/// <remarks>
/// The relay validates incoming bearer JWTs against the configured external IdP's
/// JWKS document (auto-fetched from <c>{Authority}/.well-known/openid-configuration</c>).
/// No user database — Stampd trusts what the IdP signs.
/// </remarks>
public sealed class OidcRelayOptions
{
    /// <summary>
    /// External IdP issuer URL. Used both as the <c>iss</c> claim validator and as
    /// the base for OpenID discovery / JWKS retrieval. Required.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Expected <c>aud</c> claim value. The IdP must be configured to issue tokens
    /// with this audience for Stampd's WebApi. Required.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Claim name on the external token that carries the user's role/group memberships.
    /// Defaults to <c>groups</c>; common alternatives are <c>roles</c> or a vendor-specific
    /// claim like <c>cognito:groups</c>.
    /// </summary>
    public string RoleClaim { get; set; } = "groups";

    /// <summary>
    /// Translation from external role/group names to Stampd's role taxonomy
    /// (<c>Admin</c> / <c>Sender</c> / <c>ReadOnly</c>). Names not in this map are dropped —
    /// adopters are forced to be explicit about who gets what.
    /// </summary>
    public Dictionary<string, string> RoleClaimMappings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional claim name carrying the tenant the user belongs to. When present and
    /// parseable as a GUID, on first sign-in of an admin user we insert a matching
    /// <c>AdminScope</c> row. Defaults to <c>tenant_id</c>.
    /// </summary>
    public string TenantClaim { get; set; } = "tenant_id";

    /// <summary>
    /// When false (default), tokens missing the configured <see cref="TenantClaim"/> are
    /// accepted but no auto-scope happens. When true, missing tenant claims cause 401 —
    /// useful for adopters who require every IdP-issued token to be tenant-bound.
    /// </summary>
    public bool RequireTenantClaim { get; set; }
}

/// <summary>
/// v3.0 alpha.2 — auth-mode selector. Bound from <c>Stampd:Auth:Mode</c>.
/// </summary>
public enum StampdAuthMode
{
    /// <summary>
    /// In-process JWT minter for Development. Throws at startup outside the
    /// <c>Development</c> environment so production deployments are forced onto a
    /// real SSO mode.
    /// </summary>
    DevJwt = 0,

    /// <summary>
    /// Validate JWTs issued by an external OIDC IdP. Requires
    /// <see cref="OidcRelayOptions.Authority"/> + <see cref="OidcRelayOptions.Audience"/>.
    /// </summary>
    Oidc = 1,
}
