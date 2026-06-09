# Stampd.Identity.Oidc

v3.0 alpha.2 — OIDC relay for Stampd.WebApi.

Validates incoming bearer JWTs against any OpenID Connect compatible identity provider (Auth0, Okta, Azure AD, Google, Keycloak, etc.) and maps external claims into Stampd's role + scope model. Replaces the in-process dev JWT minter for production deployments.

## Configuration

```jsonc
{
  "Stampd": {
    "Auth": {
      "Mode": "Oidc",
      "Oidc": {
        "Authority": "https://example.auth0.com/",
        "Audience": "https://api.stampd.example.com",
        "RoleClaim": "groups",
        "RoleClaimMappings": {
          "stampd-admins": "Admin",
          "stampd-senders": "Sender",
          "stampd-readonly": "ReadOnly"
        },
        "TenantClaim": "tenant_id",
        "RequireTenantClaim": false
      }
    }
  }
}
```

External group/role names not in `RoleClaimMappings` are dropped silently — adopters must be explicit about who gets what.

## What the relay does NOT do

- It doesn't host an OIDC server. Stampd is a relying party / resource server, not an identity provider.
- It doesn't manage users. Stampd has no user table; the IdP is the source of truth.
- It doesn't issue tokens. Tokens come from your IdP; Stampd only validates.
