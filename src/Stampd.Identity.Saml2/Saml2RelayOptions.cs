namespace Stampd.Identity.Saml2;

/// <summary>
/// v3.0 alpha.3 — config for the SAML2 Service Provider. Bound from
/// <c>Stampd:Auth:Saml2</c>.
/// </summary>
/// <remarks>
/// SAML2 federation flow: Stampd is the SP, customer's IdP (Okta / Azure AD /
/// Ping / ADFS) issues signed assertions. On a successful ACS POST Stampd
/// validates the assertion, extracts the NameID + attribute statements, and
/// maps them into Stampd's role + scope model. No user database; the IdP is
/// the source of truth.
/// </remarks>
public sealed class Saml2RelayOptions
{
    /// <summary>
    /// SP entity ID — the value Stampd publishes in its metadata and that the
    /// IdP must use as the assertion's audience. Conventional shape is the
    /// public URL of the WebApi (e.g. <c>https://api.stampd.example.com</c>).
    /// Required.
    /// </summary>
    public string SpEntityId { get; set; } = string.Empty;

    /// <summary>
    /// IdP entity ID — the issuer value the IdP includes in its assertions.
    /// Pulled from the IdP's metadata document. Required.
    /// </summary>
    public string IdpEntityId { get; set; } = string.Empty;

    /// <summary>
    /// URL of the IdP's published metadata document (XML). Stampd fetches this
    /// at startup to discover signing keys, SSO endpoint, and supported bindings.
    /// Required.
    /// </summary>
    public string IdpMetadataUrl { get; set; } = string.Empty;

    /// <summary>
    /// Path to the SP's signing certificate (PFX). Used to sign AuthnRequests
    /// and (optionally) decrypt incoming assertions. Optional — many IdPs
    /// don't require AuthnRequest signing.
    /// </summary>
    public string? SigningCertificatePath { get; set; }

    /// <summary>Password protecting <see cref="SigningCertificatePath"/>.</summary>
    public string? SigningCertificatePassword { get; set; }

    /// <summary>
    /// SAML attribute name that carries the user's role/group memberships.
    /// Defaults to <c>http://schemas.microsoft.com/ws/2008/06/identity/claims/role</c>
    /// (Azure AD / ADFS standard). Common alternatives are <c>groups</c> for Okta
    /// or vendor-specific names.
    /// </summary>
    public string RoleClaim { get; set; } = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    /// <summary>
    /// Translation from external role/group names to Stampd's role taxonomy
    /// (Admin / Sender / ReadOnly). Same explicit-opt-in semantics as the OIDC
    /// relay — unmapped names are silently dropped.
    /// </summary>
    public Dictionary<string, string> RoleClaimMappings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// SAML attribute name carrying the tenant the user belongs to. Defaults
    /// to <c>tenant_id</c>. Null/missing → no auto-scope.
    /// </summary>
    public string TenantClaim { get; set; } = "tenant_id";

    /// <summary>
    /// When true, missing tenant claim causes the SAML ACS handler to reject
    /// the assertion. Default false (accept-but-unscoped).
    /// </summary>
    public bool RequireTenantClaim { get; set; }
}
