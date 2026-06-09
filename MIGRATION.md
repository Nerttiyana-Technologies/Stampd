# Migrating Stampd v2.x → v3.0.0

v3.0 is a MAJOR release. This document covers the upgrade path for adopters running v2.3.0 stable.

## TL;DR

1. Apply V16 migration on every database provider (adds `AdminScopes` table).
2. Decide auth mode: `DevJwt` stays for Development; production must pick `Oidc` or `Saml2`.
3. (Optional) Add `Stampd.Compliance` if you need HIPAA / 21 CFR Part 11 / eIDAS QES gating.
4. Bootstrap at least one admin scope (`Stampd:Auth:SuperAdminUserIds` or insert into `AdminScopes`).

Everything else is additive.

## Breaking changes

### 1. The `Admin` authorization policy now requires BOTH the Admin role claim AND an active `AdminScope` row

v2.x: any JWT carrying `role: Admin` was tenant-wide admin on whatever tenant context the request landed in.

v3.0: same JWT also needs a row in the new `AdminScopes` table for the request's tenant — OR the user's JWT `sub` must be in `Stampd:Auth:SuperAdminUserIds`.

**Action required.** Two paths:

- **Quick (compatibility mode)**: list your existing admin user IDs in `Stampd:Auth:SuperAdminUserIds`. They keep working tenant-wide as before.
- **Proper RBAC**: insert `AdminScope` rows per (admin user, tenant). Use `POST /api/admin/scopes` once you've got one super-admin bootstrapped.

Without either step, the FIRST `/api/admin/*` request after upgrade returns 403.

### 2. `Stampd:Auth:Mode=DevJwt` throws at startup outside Development

Production deployments that left the v2.x dev JWT minter on get a clear startup error. Set `Stampd:Auth:Mode=Oidc` (most common) or `Stampd:Auth:Mode=Saml2` (enterprise).

`Mode=DevJwt` still works in `Development` and `Testing` environments — your existing local + CI flows are unchanged.

### 3. New required config when Mode=Oidc

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
          "stampd-senders": "Sender"
        }
      }
    }
  }
}
```

External groups not listed in `RoleClaimMappings` are silently dropped. Adopters must explicitly opt every group → role.

## Additive surface

### `Stampd.Identity.Saml2` (alpha.3 scaffold)

Endpoints `/api/auth/saml/{metadata,login,acs}` are mapped and return 501 until v3.0.1 wires up real SAML2 SP. Eager config validation runs today so misconfig fails at startup. Adopters who need real SAML2 should pin to v3.0.0 and wait for the v3.0.1 patch — or contribute the ITfoxtec wiring; the surface is in place.

### `Stampd.Compliance` (new package)

Optional. Three industry bundles with runtime-enforced gates:

| Bundle | Min signature | IV required | Audit retention |
|---|---|---|---|
| `Hipaa` | B-LT | ✅ | 6 years |
| `Cfr21Part11` | B-LTA | ✅ | 7 years |
| `EidasQes` | B-LTA | ✅ | 10 years |

Adopters who need any of these:

```csharp
services.AddStampdCompliance();
// ...then in dispatch:
gate.Validate(new ComplianceDeclaration(...));
```

Persistent `SigningRequest.ComplianceBundle` column lands in v3.1 along with the maintenance worker that prunes audit rows older than the retention window.

## Database schema

V16 migration adds the `AdminScopes` table only. Apply on every provider you run:

```bash
dotnet ef database update --project src/Stampd.Infrastructure.Sqlite    --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.SqlServer --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.Postgres  --startup-project src/Stampd.WebApi
```

The table is cross-tenant by design — no global query filter. Don't add one in a custom DbContext subclass.

## Wire-format compatibility

- v2.x JWT shape unchanged. Same `role` + `sub` claims; v3.0 just adds a second gate after the role check.
- v2.x API surface unchanged. No endpoints renamed or removed. New endpoints (`/api/admin/scopes/*`, `/api/auth/saml/*`) are additive.
- v2.x response shapes unchanged. Analytics widgets etc. work identically.

## What v3.0.0 doesn't ship

- **Workflow rules engine** — moved to v3.1 candidates. The breadth of the rules DSL + designer UI deserved a dedicated alpha cycle that wouldn't fit in the v3.0 train.
- **SAML2 SP production wiring** — v3.0.0-alpha.3 scaffolding only. Real assertion validation in v3.0.1 or via community contribution.
- **Persistent `SigningRequest.ComplianceBundle` column** — declared per dispatch in v3.0; v3.1 adds the schema column.
- **PHI redaction for HIPAA** — existing GDPR-erase flow handles most cases; HIPAA-specific classification is post-v3.

## Rollback

v3.0.0 is reversible: drop the AdminScopes table, revert to v2.3.0 packages, restore Mode=DevJwt. No data migration is destructive.
