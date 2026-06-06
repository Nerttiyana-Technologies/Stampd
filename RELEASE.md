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

For pre-releases, set `<VersionSuffix>preview.1</VersionSuffix>` (or `rc.1`, `beta.1`, etc.) in the same file. The resulting package versions look like `1.3.0-preview.1`.

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

### 2. GitHub repo secret

1. Go to <https://github.com/isureshsubramanian/Stampd/settings/secrets/actions>.
2. Click **New repository secret**.
3. Name: `NUGET_API_KEY`. Value: the key from step 1.

## Cutting a release

### 1. Bump the version

Edit `Directory.Build.props`:

```xml
<Version>1.3.0</Version>
```

Commit:

```bash
git add Directory.Build.props
git commit -m "chore(release): bump version to 1.3.0"
```

### 2. Verify locally

Pack against the live source and inspect what nuget.org would see:

```bash
dotnet pack Stampd.slnx --configuration Release --output ./artifacts
ls artifacts/                                # 21 .nupkg + 21 .snupkg
unzip -p artifacts/Stampd.Core.1.3.0.nupkg Stampd.Core.nuspec | head -40
```

Sanity-check: every `.nupkg` is present, descriptions look right, the README opens with the right text on nuget.org's preview.

### 3. Tag and push

```bash
git tag v1.3.0
git push origin main
git push origin v1.3.0
```

The tag push triggers `.github/workflows/release.yml`. The workflow:

1. Checks out the tagged commit.
2. Installs the .NET SDK from `global.json`.
3. `dotnet restore` / `build` / `test` (engine regression suite must pass).
4. **Verifies the tag matches `Directory.Build.props` `<Version>`.** If they disagree the job fails — protects against a mis-tagged push silently shipping the wrong number.
5. `dotnet pack`.
6. `dotnet nuget push './artifacts/*.nupkg' --api-key $NUGET_API_KEY --source nuget.org --skip-duplicate`.

Watch the run at <https://github.com/isureshsubramanian/Stampd/actions>.

### 4. Smoke-test the published packages

After the run finishes (typically 3–5 minutes), allow nuget.org's indexer ~10 more minutes, then:

```bash
mkdir /tmp/stampd-smoke && cd /tmp/stampd-smoke
dotnet new console -n SmokeTest
cd SmokeTest
dotnet add package Stampd.Core --version 1.3.0
dotnet add package Stampd.Engine --version 1.3.0
dotnet add package Stampd.Crypto.LocalCertificate --version 1.3.0
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

After yanking, bump `Directory.Build.props` to the next patch (e.g. `1.3.0 → 1.3.1`) and re-release. Never reuse a version number.

## Upgrade notes

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
<Version>1.3.0</Version>
<VersionSuffix>preview.1</VersionSuffix>
```

```bash
git commit -am "chore(release): 1.3.0-preview.1"
git tag v1.3.0-preview.1
git push origin main v1.3.0-preview.1
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
