# Stampd.Identity.Saml2

v3.0 alpha.3 — SAML2 Service Provider scaffolding for Stampd.WebApi.

**This package is a scaffold for v3.0.0 stable.** Config DTO, claim mapper, endpoint surface, and eager validation guards are in place. The actual SAML assertion validation + ACS handler wiring (signature, encryption, replay cache, audience binding) lands in v3.0.0 stable.

Adopters configuring `Stampd:Auth:Mode=Saml2` today:
- Get the eager config validation (missing keys → clear startup error).
- See the endpoint URLs Stampd will use (`/api/auth/saml/metadata`, `/login`, `/acs`).
- Get 501 Not Implemented from those endpoints until v3.0.0 stable.

## Configuration

```jsonc
{
  "Stampd": {
    "Auth": {
      "Mode": "Saml2",
      "Saml2": {
        "SpEntityId": "https://api.stampd.example.com",
        "IdpEntityId": "https://login.microsoftonline.com/{tenant}/",
        "IdpMetadataUrl": "https://login.microsoftonline.com/{tenant}/federationmetadata/2007-06/federationmetadata.xml",
        "SigningCertificatePath": "/etc/stampd/saml-sp.pfx",
        "SigningCertificatePassword": "***",
        "RoleClaim": "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        "RoleClaimMappings": {
          "stampd-admins": "Admin",
          "stampd-senders": "Sender"
        },
        "TenantClaim": "tenant_id",
        "RequireTenantClaim": false
      }
    }
  }
}
```

## Roadmap

- **alpha.3** (this release) — scaffold + eager config validation + scoped to defining the wire format.
- **v3.0.0 stable** — full assertion validation via ITfoxtec.Identity.Saml2 or equivalent, ACS handler that mints a Stampd-issued JWT from a successful SAML round-trip.
