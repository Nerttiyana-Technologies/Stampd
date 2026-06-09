# Release Process

How Stampd's library packages get to nuget.org.

## What gets published

The following 21 library projects are published as NuGet packages on every release:

| Package | Description |
|---|---|
| `Stampd.Core` | Domain types and contracts. Zero runtime dependencies. |
| `Stampd.Engine` | PdfSharp + BouncyCastle PAdES engine. |
| `Stampd.Infrastructure` | EF Core DbContext + entity config. Provider-agnostic. |
| `Stampd.Infrastructure.Sqlite` | SQLite provider + migrations. |
| `Stampd.Infrastructure.SqlServer` | SQL Server provider + migrations. |
| `Stampd.Infrastructure.Postgres` | PostgreSQL provider + migrations. |
| `Stampd.Crypto.LocalCertificate` | Local X.509 sealing provider. |
| `Stampd.Crypto.Vault` | HashiCorp Vault / OpenBao Transit sealing. |
| `Stampd.Crypto.AzureKeyVault` | Azure Key Vault HSM sealing. |
| `Stampd.Crypto.AwsKms` | AWS KMS sealing. |
| `Stampd.Timestamp.Rfc3161` | Generic RFC 3161 TSA provider. |
| `Stampd.Timestamp.FreeTsa` | FreeTSA preset. |
| `Stampd.Storage.FileSystem` | Local-filesystem document storage. |
| `Stampd.Storage.S3` | AWS S3 document storage. |
| `Stampd.Storage.AzureBlob` | Azure Blob Storage document storage. |
| `Stampd.Storage.Gcs` | Google Cloud Storage document storage. |
| `Stampd.Revocation.Http` | OCSP + CRL HTTP fetchers. |
| `Stampd.Email.Smtp` | MailKit-backed SMTP IEmailSender. |
| `Stampd.Identity.EmailOtp` | Email-OTP IIdentityVerificationProvider. |
| `Stampd.Identity.SmsOtp` | SMS-OTP IIdentityVerificationProvider. |
| `Stampd.Identity.Kba` | KBA IIdentityVerificationProvider. |

`Stampd.WebApi`, `Stampd.UI`, the smoke sample, and the test project carry `<IsPackable>false</IsPackable>` and are not published.

## Version source of truth

The version lives in **`Directory.Build.props`** in the `<Version>` element. Every library inherits from it, so a single bump propagates to all 21 packages.

For pre-releases, set `<VersionSuffix>preview.1</VersionSuffix>` (or `rc.1`, `beta.1`, etc.) in the same file. The resulting package versions look like `2.0.0-preview.1`.

## One-time setup (do this once per maintainer)

### 1. nuget.org account + API key

1. Sign in at <https://www.nuget.org> (Microsoft account / GitHub).
2. Verify the account owns (or is a co-owner of) `Stampd.Core` — if this is the first release, the API key creates the package and the signed-in account becomes its owner.
3. Generate an API key:
   - Click your username → **API Keys** → **Create**.
   - Key Name: `stampd-release-ci`.
   - Scope: **Push** + **Push new packages and package versions**.
   - Glob Pattern: `Stampd.*`.
   - Expiration: 365 days (max).
   - Copy the key. **You can't view it again.**

## Cutting a release

### 1. Bump the version

Edit `Directory.Build.props`:

```xml
<Version>2.0.0</Version>
```

Commit:

```bash
git add Directory.Build.props
git commit -m "chore(release): bump version to 2.0.0"
```

### 2. Verify locally

Pack against the live source and inspect what nuget.org would see:

```bash
dotnet pack Stampd.slnx --configuration Release --output ./artifacts
ls artifacts/                                # 21 .nupkg + 21 .snupkg
unzip -p artifacts/Stampd.Core.2.0.0.nupkg Stampd.Core.nuspec | head -40
```

Sanity-check: every `.nupkg` is present, descriptions look right, the README opens with the right text on nuget.org's preview.

### 3. Tag and push

```bash
git tag v2.0.0
git push origin main
git push origin v2.0.0
```

The tag push triggers `.github/workflows/release.yml`. The workflow:

1. Checks out the tagged commit.
2. Installs the .NET SDK from `global.json`.
3. `dotnet restore` / `build` / `test` (engine regression suite must pass).
4. **Verifies the tag matches `Directory.Build.props` `<Version>`.** If they disagree the job fails — protects against a mis-tagged push silently shipping the wrong number.
5. `dotnet pack`.
6. `dotnet nuget push './artifacts/*.nupkg' --api-key $NUGET_API_KEY --source nuget.org --skip-duplicate`.


### 4. Smoke-test the published packages

After the run finishes (typically 3–5 minutes), allow nuget.org's indexer ~10 more minutes, then:

```bash
mkdir /tmp/stampd-smoke && cd /tmp/stampd-smoke
dotnet new console -n SmokeTest
cd SmokeTest
dotnet add package Stampd.Core --version 2.0.0
dotnet add package Stampd.Engine --version 2.0.0
dotnet add package Stampd.Crypto.LocalCertificate --version 2.0.0
dotnet build
```

A clean restore + build from nuget.org's CDN confirms the packages are discoverable and their dependencies resolve.

## Dry-run from GitHub Actions

The release workflow accepts `workflow_dispatch` so you can pack-without-push from main without tagging:

1. Actions → **Release** → **Run workflow** → branch `main` → check **Pack only, do not push**.
2. The job runs build/test/pack, uploads the resulting `.nupkg` files as a workflow artifact, and skips the push step.

Useful for validating packaging changes before tagging.

## Yanking a release

Mistakes happen. From the nuget.org package page, click **Manage** → **Listed: Yes → No** on the bad version. The package stays available for adopters who already pinned it (NuGet's deprecation contract) but disappears from search and from `dotnet add package` without an explicit version.

After yanking, bump `Directory.Build.props` to the next patch (e.g. `2.0.0 → 2.0.1`) and re-release. Never reuse a version number.

## Upgrade notes

### Upgrading from v2.3.0 → v3.0.0-alpha.1

v3.0 ships as a series of pre-release alphas before the stable tag. **alpha.1 is opt-in only** — `dotnet add package Stampd.* --prerelease --version 3.0.0-alpha.1`. v2.3 stable remains the recommendation for production until v3.0.0 stable lands.

v3.0 is a MAJOR release. Read [`internal/implementation/34-v30-plan.md`](internal/implementation/34-v30-plan.md) for the full v3.0 sequence (alpha.1 → alpha.4 → stable) and stop-points if you want to track partial v3 progress.

#### 1. Apply V16 migration on every provider

Adds the `AdminScopes` table with composite `(UserId, TenantId)` and `(TenantId)` indexes. No backfill — first-run admins must be granted scope explicitly (via the new endpoints) or listed in the `Stampd:Auth:SuperAdminUserIds` bootstrap config.

```bash
dotnet ef database update --project src/Stampd.Infrastructure.Sqlite    --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.SqlServer --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.Postgres  --startup-project src/Stampd.WebApi
```

#### 2. Per-tenant admin enforcement is now live

The `Admin` authorization policy gained a second gate. Beyond carrying the `role: Admin` JWT claim, the caller must also hold an active `AdminScope` row for the current request's tenant. Without one, `/api/admin/*` endpoints return 403.

**Two friction-free upgrade paths**:

- **Quick**: set `Stampd:Auth:SuperAdminUserIds` to a comma-separated list of your existing admin JWT `sub` values. These users bypass per-tenant scope checks. Use as bootstrap; tighten later.
- **Proper RBAC**: insert `AdminScopes` rows for each admin/tenant pair via the new `POST /api/admin/scopes` endpoint. Requires at least one super-admin to grant the first scopes.

#### 3. New endpoints under `/api/admin/scopes`

- `POST /api/admin/scopes` — grant. Body: `{ "userId": "..." }`. Tenant comes from the request context.
- `DELETE /api/admin/scopes/{id}` — revoke. Body: `{ "reason": "..." }` (optional).
- `GET /api/admin/scopes?activeOnly=true` — list, optionally filtered to active only.

All three require the new `Admin` policy and the super-admin override if you're crossing tenant boundaries.

#### 4. New config key `Stampd:Auth:SuperAdminUserIds`

Comma-separated JWT `sub` values that bypass scope checks. Default empty (no super-admins). Adopters who don't set it need to bootstrap via raw DB inserts on the AdminScopes table.

#### 5. NOT in alpha.1

- No UI for grant / revoke / list. Power users hit the API directly. UI lands in a later alpha or v3.0.0 stable.
- No cross-tenant scope migration tool. If you have multi-tenant data, write a seed script.

### Upgrading from v2.2.0 → v2.3.0

v2.3 is a MINOR release. Two new endpoints (`/api/admin/analytics/webhooks-health`, `/api/admin/analytics/by-sender`), one new endpoint file (`AdminAuditExportEndpoints` with `GET /api/admin/audit/export`), and one additive field on the funnel response (`byDayBucket`). Zero schema changes, zero new migrations, zero new config knobs required.

#### 1. New endpoint `GET /api/admin/analytics/webhooks-health`

Returns counter tiles (`totalEndpoints`, `activeEndpoints`, `degradedEndpoints`, `pendingDeliveries`, `retryingDeliveries`, `failuresLast24h`) plus a top-10 `recentFailures` array carrying the failing endpoint URL, event type, attempt count, last HTTP status, last error message, and next-attempt timestamp. Inherits the `Admin` policy from the admin group. No request body, no params.

The `/admin` dashboard renders a new "Webhook health" card between the analytics section and the danger zone. Adopters who don't use webhooks see four zero tiles — harmless.

#### 2. New endpoint `GET /api/admin/analytics/by-sender?days=30&take=10`

Returns `items[]` of `{ sender, dispatched, completed, voided, completionRatePercent, avgTimeToSignMinutes }` ordered by dispatched desc. `sender` is the raw `SigningRequest.CreatedBy` string (your JWT `sub` claim). `days` clamps to 1-365 with default 30; `take` clamps to 1-50 with default 10.

The dashboard renders a "Per-sender productivity" table inside the existing analytics section. Two queries per call, both index-covered.

#### 3. New endpoint `GET /api/admin/audit/export`

Streams the tenant's audit trail as RFC 4180-ish CSV with `Content-Disposition: attachment` so browsers prompt to save. Optional filters: `from`/`to` (`DateTimeOffset` ISO-8601), `eventType` (case-insensitive `AuditEventType` name), `actorUserId` (exact match), `maxRows` (clamped to `Stampd:Admin:MaxAuditExportRows`, default 250,000).

`Stampd:Admin:MaxAuditExportRows` is a new optional config key. Default keeps the export bounded so a single download doesn't take 20 minutes; adopters with adoption-scale data can raise it. Adopters who don't set the key get the 250k default.

The dashboard's new "Audit-trail export" card has two `<a download>` links — "last N days" using the current window selector, and "all (capped at 250k)" for a full export. The browser hits the WebApi directly so the CSV streams to disk without round-tripping through Blazor.

#### 4. Funnel response gains a `byDayBucket` field

`GET /api/admin/analytics/funnel` now returns `byDayBucket: { weekday: {...}, weekend: {...} }` alongside the existing fields. Each bucket carries `invited` / `signed` / `conversionRatePercent` for SigningRequests dispatched on Mon-Fri UTC vs. Sat-Sun UTC. Nullable on the wire — pre-v2.3 UI clients deserialize the response with the field absent and see no behavior change.

UTC bucketing is deliberate — recipient timezone isn't available at dispatch time. Adopters who care about local-time bucketing should compute it from their own dispatch timestamps; the field surfaces the tenant-wide view.

#### 5. No migration step

v2.3 ships zero schema changes. `dotnet add package Stampd.* --version 2.3.0` and the new endpoints + dashboard sections come up immediately. Pre-v2.3 UI clients keep working against a v2.3 WebApi because every response shape change is additive and nullable.

### Upgrading from v2.1.0 → v2.2.0

v2.2 is a MINOR release — analytics-only, fully additive. No schema changes, no new migrations, no new config flags. Every change is backwards-compatible on the wire: every new field in the analytics responses is nullable, so adopters running an older Stampd.UI against a v2.2 WebApi (or vice versa) see the existing widgets unchanged.

#### 1. Analytics endpoints return prior-window comparison alongside current values

All three `/api/admin/analytics/*` endpoints now run their aggregation twice per call — once for the requested window, once for the equally-sized prior window — and surface both plus a delta. Funnel and identity-verification responses gain `previousWindow` + `deltas` blocks; time-to-sign gains per-template `previousWindow` + `avgMinutesDeltaPercent` + `medianMinutesDeltaPercent` (null when a template is new in the current window).

The second query is index-covered on `(TenantId, CreatedAtUtcEpochMs)` so the marginal cost is sub-millisecond on any realistic data volume. Adopters who don't want the prior-window work can ignore the new fields — the existing ones are unchanged.

#### 2. Identity-verification endpoint gains a per-channel breakdown

`GET /api/admin/analytics/identity-verification` returns a new `byChannel` block with `email` / `sms` / `kba` splits, each carrying `initiatesCount` + `verifiedCount` + `lockedOutCount`. Channel is derived without a schema change:

- **Email / SMS** — classified from `OtpChallenge.Identifier` shape (contains `@` → Email, else SMS).
- **KBA** — pulled from `Recipient.IdentityVerificationMethod` (KBA bypasses the OTP store entirely). KBA `initiatesCount` is proxied from its verified count since there's no separate attempt log; the UI footnote calls this out.

The `/admin` dashboard renders the breakdown as two small stacked bars under the existing IV tiles, so admins can spot which method is taking brute-force pressure.

#### 3. Funnel endpoint adds a request-weighted completion view

The recipient-level funnel that's been in place since v2.0 stays unchanged — `signed / invited` is still per-recipient and reads correctly for single-signer workflows. v2.2 adds a sibling `requestWeighted` block that computes `signed ÷ recipients` per SigningRequest and averages across requests, so a 2-of-3-signed request contributes 0.67 to the metric instead of 0 or 1. Useful for tenants who run multi-recipient workflows and want a fairer "how far have we gotten" number than the binary "all signed / not all signed" view.

Surfaces `weightedCompletionPercent`, `fullyCompletedRequests`, and a `requestCount` denominator. Prior-window comparison applies here too.

#### 4. Time-to-sign endpoint adds per-role segmentation

`GET /api/admin/analytics/time-to-sign` returns a new `byRole` array alongside the existing per-template `items`. Each row aggregates recipients across all templates by `Recipient.Role.Name` (the template-level role name like "Customer" or "Approver", NOT the RBAC role) and reports avg/median time-to-sign plus prior-window deltas. Recipients without a role (legacy data with null `RoleId`) bucket under `(unassigned)`.

The dashboard renders the per-role table beneath the per-template one. Lets admins answer "do Approvers always take longer than Signers?" at a tenant level instead of digging through individual templates.

#### 5. Per-cell delta semantics in the dashboard

Delta chips use green ▲ for improvements and red ▼ for regressions, but the meaning of "improvement" depends on the metric — higher conversion is good, lower lockout count is good, lower time-to-sign is good. The dashboard's `RenderDeltaPercent` helper carries a `lowerIsBetter` flag for this. No adopter action; mentioned for anyone forking the dashboard.

### Upgrading from v2.0.0 → v2.1.0

v2.1 is a MINOR release — additive, fully backwards-compatible at the API surface. Two visible changes for adopters: a new EF migration, and a new opt-in TSA failover config flag.

#### 1. Apply V15 migration on every provider

`SigningRequest` gains one column — `CompletedAtUtcEpochMs` (`bigint`, nullable) — plus a covering composite index `(TenantId, CompletedAtUtcEpochMs)`. The column is the strict, server-side-sortable epoch shadow of `CompletedAtUtc`, replacing v2.0's "route completed-sort through CreatedAtUtcEpochMs" workaround in `GET /api/signing-requests`. Historical rows backfill in the same transaction (`julianday()` on SQLite, `DATEDIFF_BIG` on SqlServer, `EXTRACT(EPOCH FROM ...)` on Postgres). In-flight workflows stay null.

```bash
dotnet ef database update --project src/Stampd.Infrastructure.Sqlite    --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.SqlServer --startup-project src/Stampd.WebApi
dotnet ef database update --project src/Stampd.Infrastructure.Postgres  --startup-project src/Stampd.WebApi
```

Skip the providers you don't run. The migration is online-safe: column is nullable, backfill is `WHERE CompletedAtUtc IS NOT NULL`, index creation is non-blocking on Postgres/SqlServer.

#### 2. Opt-in DigiCert TSA failover

`Stampd.WebApi` gained a new config flag, `Stampd:Tsa:EnableDigiCertFailover` (default `false`). When `true`, the registered primary TSA (FreeTSA or any Rfc3161-configured endpoint) is wrapped in a `FailoverTimestampAuthorityProvider` that falls back to DigiCert's free public TSA (`http://timestamp.digicert.com`) if the primary throws, times out, or refuses. Designed to absorb FreeTSA's occasional outages without operator intervention.

Enable it in `appsettings.Production.json`:

```jsonc
{
  "Stampd": {
    "Tsa": {
      "EnableDigiCertFailover": true
    }
  }
}
```

Default `false` preserves existing v2.0 behavior — adopters who don't flip the flag see zero change.

#### 3. PdfSharp PNG alpha handling

The engine now normalizes incoming PNG signature/initial images through SkiaSharp before handing them to PdfSharp's `XImage` reader. Closes a v2.0 bug where PdfSharp 6.x rendered alpha=0 pixels as opaque black, producing a black rectangle behind visibly-transparent signatures. JPEG and pre-flat PNGs are passthrough; only PNGs with an alpha channel pay the re-encode cost (~5ms for a typical signature image). No adopter action — the fix is transparent to callers.

#### 4. `/designer/requests` filter state mirrored into URL

The Blazor admin list page now syncs its filter / sort / page state into the address bar query string. Copying the URL gives a shareable link that restores the same view; the browser back button navigates filter history. Pure UI change — the API surface is unchanged.

#### 5. `GET /api/signing-requests?sortBy=completed` is now strict

In v2.0 the `completed` sort routed through `CreatedAtUtcEpochMs` as a proxy (because EF Core 10's SQLite provider can't translate ORDER BY on a `DateTimeOffset?`). With V15's epoch shadow in place, it now sorts by actual completion time. In-flight workflows (`CompletedAtUtcEpochMs IS NULL`) are pushed to the trailing bucket in both directions, with `CreatedAtUtcEpochMs` as a stable tiebreak. Adopters who depended on the v2.0 proxy behavior should re-validate any saved sort URLs — they'll now return the order they were always meant to.

### Upgrading from v1.3.0 → v2.0.0

v2.0 is a MAJOR release. The library API surface stays backwards-compatible (no method signatures removed or renamed), but the deployment shape changes in three ways adopters need to plan for: a new EF migration, new authorization policies enforced on admin endpoints, and a new role claim required in production JWTs.

#### 1. Apply V14 migration on every provider

`AuditEvent` gains two columns — `ActorUserId` (nvarchar(256), nullable) and `ActorRole` (nvarchar(64), nullable) — plus a composite index `(TenantId, ActorUserId, OccurredAtUtc)` to support the "which admin did how many things in window N" query shape. The columns are nullable; historical rows backfill to NULL (no synthetic attribution).

```bash
dotnet ef database update --project src/Stampd.Infrastructure.Sqlite
dotnet ef database update --project src/Stampd.Infrastructure.SqlServer
dotnet ef database update --project src/Stampd.Infrastructure.Postgres
```

Skip the providers you don't run. Stampd starts up against an un-migrated database, but write paths that emit audit rows will throw on column-not-found until the migration applies.

#### 2. New admin endpoints are gated by the `Admin` policy

The admin dashboard, analytics, and bulk-operations endpoints under `/api/admin/*` enforce `RequireAuthorization("Admin")`. The policy is registered automatically in `Program.cs` and requires the authenticated principal to carry a `role` claim with value `Admin`. The role taxonomy:

- **Admin** — full read + write + admin-tile + bulk-operations + demo cleanup.
- **Sender** — can create templates, dispatch signing requests, see their own requests.
- **ReadOnly** — list/view only, no writes.

The dev JWT minter (`POST /api/auth/dev-token`, available only when `ASPNETCORE_ENVIRONMENT=Development`) accepts a `roles` array in the request body so existing demo flows continue to work end-to-end. Production deployments using the standard JWT bearer middleware must add the `role` claim to issued tokens — the role string lands at `ClaimTypes.Role` (`http://schemas.microsoft.com/ws/2008/06/identity/claims/role`).

A principal with no role claim has read access to the workflow surfaces but is rejected from `/api/admin/*`. This is additive — adopters not using the admin dashboard see no behavior change.

#### 3. `AuditEvent.ActorUserId` + `ActorRole` populated automatically

The `SigningWorkflowService` now resolves the current authenticated principal via `ICurrentActorContext` (HTTP-context-backed in the WebApi) and stamps every audit row with `ActorUserId` + `ActorRole`. Recipient-side events (where auth is the per-recipient access token, not an authenticated user) still write `NULL` for both columns. No adopter action required — the wiring is transparent.

If you have a custom host that does NOT use the WebApi composition root, register an `ICurrentActorContext` implementation in DI. The default `HttpCurrentActorContext` reads from `IHttpContextAccessor.HttpContext.User`.

#### 4. `GET /api/signing-requests` accepts new optional query parameters

Slice B added `status`, `senderEmail`, `recipientEmail`, `dispatchedFrom`, `dispatchedTo`, `sortBy`, and `direction` query parameters. All are optional and backwards-compatible — clients on v1.3 contracts continue to receive the same paged envelope shape. The `completed` sort key routes through `CreatedAtUtcEpochMs` as a proxy due to an EF Core SQLite translation limit on nullable `DateTimeOffset` ordering; v2.1 (#212) adds a dedicated `CompletedAtUtcEpochMs` shadow column to fix that.

#### 5. UI: theme persistence across navigation

The Blazor UI fixes a v1.3 issue where the user's theme choice (light/dark) was wiped on every page transition because Blazor enhanced-navigation patches `<html>` attributes against the server-rendered shell. The fix is JS-only — head-inline restore script + `enhancedload` listener + MutationObserver guard. No config knobs, no adopter action.

### Upgrading from v1.2.0 → v1.3.0

v1.3 is a MINOR release — additive on the API and DI surface, but adopters running specific provider combinations should know about three concrete migration concerns.

#### 1. SqlServer and Postgres providers must apply V12 + V13 sequentially

`Stampd.Infrastructure.SqlServer` 1.2.0 and `Stampd.Infrastructure.Postgres` 1.2.0 shipped at migration V11 — they were missing the V12 epoch-sort migration that the SQLite provider got at v1.2. v1.3 ships both V12 and V13 catch-up migrations on those providers. Anyone running SqlServer or Postgres in production on v1.2.0 will see a SCHEMA mismatch on startup at v1.3 because the entity model declares `EpochMs` columns that don't exist in their database.

Apply both migrations as part of the upgrade:

```bash
dotnet ef database update --project src/Stampd.Infrastructure.SqlServer
dotnet ef database update --project src/Stampd.Infrastructure.Postgres
```

Each provider has its own `IDesignTimeDbContextFactory<StampdDbContext>` so no `--startup-project` is required. Both V12 and V13 include backfill SQL (`DATEDIFF_BIG` on SqlServer, `EXTRACT(EPOCH FROM ...) * 1000` on Postgres) — existing rows get their epoch columns populated in the same transaction as the column ADD. SQLite-only deployments need only V13.

#### 2. `GET /api/signing-requests` response shape changed

v1.2 returned a raw array; v1.3 returns a paged envelope. The URL and query-string surface are additive (`page` and `pageSize` are optional), but adopters parsing the response need to read `items`:

```diff
- response: SigningRequestSummary[]
+ response: { items: SigningRequestSummary[], total, page, pageSize, totalPages }
```

The in-tree Blazor UI was updated in lockstep. Direct API consumers (cURL pipelines, Postman collections, custom dashboards) should add `.items[]` to their post-processing.

#### 3. OTP rate limit + lockout defaults activate automatically

`EmailOtpProvider` and `SmsOtpProvider` now enforce a 5-per-15-minute initiate rate limit per identifier and a 5-failed-attempt lockout per challenge, with no opt-in required. This is a security improvement, but adopters who have their own upstream rate limiting may want to disable Stampd's by setting the new options to `0`:

```
Stampd:Identity:EmailOtp:InitiatesPerWindowMax=0
Stampd:Identity:EmailOtp:MaxFailedAttempts=0
```

(Same keys under `Stampd:Identity:SmsOtp:*`.) Setting either to `0` disables the corresponding gate. NOT recommended for production unless an upstream gateway is doing equivalent work.

#### 4. BLTA signing now makes 3 TSA round-trips per signature

Up from 2 in v1.2. The third call is the new PAdES Document Timestamp (Part 4 carrier). If your TSA quota is tight, plan accordingly — or target `B-LT` (1 round-trip + revocation gather) instead of `B-LTA` for documents that don't strictly need the long-term archive anchor.

#### 5. Pre-existing recursive `ApplyBearer` bug fixed

`DesignerApiClient.ApplyBearer` had a v1.2 bug that would stack-overflow on any non-empty bearer token. v1.3 fixes it. No adopter action needed if you were running with `AutoAuth=true` (Development); production-mode adopters using manual JWT entry could not have functioned with v1.2 anyway.

## Pre-release flow

```xml
<!-- Directory.Build.props -->
<Version>2.0.0</Version>
<VersionSuffix>preview.1</VersionSuffix>
```

```bash
git commit -am "chore(release): 2.0.0-preview.1"
git tag v2.0.0-preview.1
git push origin main v2.0.0-preview.1
```

Pre-release packages are listed on nuget.org but the **Latest Stable** dropdown filters them out by default. Consumers opt in with `--prerelease` on `dotnet add package` or a `*-preview.*` floating version.

## SemVer policy

- **MAJOR** — public API breaks. We won't take these lightly; expect long deprecation cycles when they do happen.
- **MINOR** — new features, additive API surface, new packages. Backwards-compatible for consumers.
- **PATCH** — bug fixes only. No API changes. No new dependencies.

The Stampd packages move in lockstep — every release ships all 21 packages at the same version, even when only one project changed. Lockstep makes adopter upgrades trivial (`dotnet add package Stampd.Engine` and you know `Stampd.Core` is the matching version) and matches the way Stampd's projects depend on each other internally.

## Where the version lives in the repo

| File | Purpose |
|---|---|
| `Directory.Build.props` | Source of truth: `<Version>` element. |
| `README.md` → Release history | Human-facing changelog. Add a row per release. |
| `internal/implementation/*.md` | Per-feature implementation notes for engineers, indexed by version. |

Always update all three when you cut a release.
