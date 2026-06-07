# Security Policy

Stampd is an e-signature platform handling cryptographic signing, identity verification, and audit-grade evidence. Security reports are taken seriously. This document explains what to report, how to report it, what to expect, and what's in scope.

## Reporting a vulnerability

**Email:** security@stampd.org (preferred) or i.suresh.subramanian@gmail.com

**GitHub Security Advisories** (alternative): open a private advisory at <https://github.com/Nerttiyana-Technologies/Stampd/security/advisories/new>.

When reporting, please include:

- A clear description of the issue and its impact
- Affected version(s) — output of `git rev-parse HEAD` or the released package version
- Reproduction steps or proof-of-concept code
- Your assessment of the severity (Critical / High / Medium / Low)
- Whether you've already disclosed this elsewhere

**Please do not file public GitHub issues for security problems.** Use email or a private advisory so a fix can ship before public disclosure.

## What to expect

| Stage | Target |
|---|---|
| Acknowledgement of receipt | Within 48 hours |
| Initial severity assessment | Within 5 business days |
| Fix targeted for | Within 30 days for Critical / High, 90 days for Medium / Low |
| CVE assignment (if applicable) | Coordinated through GitHub Security Advisories |
| Public disclosure | After a fix is released, coordinated with the reporter |

We follow [coordinated disclosure](https://en.wikipedia.org/wiki/Coordinated_vulnerability_disclosure): you tell us privately, we ship a fix, and we credit you (or not, your choice) when the fix is public.

## Supported versions

| Version | Supported |
|---|---|
| 2.x (latest) | ✅ Yes — active development + security fixes |
| 1.3.x | ✅ Yes — security fixes only |
| 1.2.x and older | ❌ No — please upgrade |

Security fixes ship as patch releases (`2.0.0 → 2.0.1`) on the affected minor line plus the latest. Backports to 1.3.x are evaluated case-by-case based on severity.

## In scope

Issues we want to know about:

- **Cryptographic flaws** in `Stampd.Engine` (PAdES signature construction, CMS attribute handling, ATSHashIndexV3 imprint, ByteRange or /Contents tampering, Document Timestamp verification)
- **Authentication / authorization bypasses** in `Stampd.WebApi` (JWT validation, RBAC policy enforcement, recipient access token verification)
- **Identity verification weaknesses** in `Stampd.Identity.*` (OTP brute-force, replay attacks, lockout bypass, KBA logic flaws)
- **Sealing provider vulnerabilities** in `Stampd.Crypto.*` (key extraction, signature forgery, HSM session handling)
- **Storage provider vulnerabilities** in `Stampd.Storage.*` (cross-tenant access, path traversal, signed-URL manipulation)
- **Webhook delivery flaws** (HMAC signature bypass, SSRF via webhook URL)
- **EF Core tenant isolation breaks** (cross-tenant query leaks)
- **Audit-log integrity issues** (events that should be recorded but aren't, or events that can be tampered with)
- **Supply-chain risks** in dependencies (vulnerable NuGet packages, malicious package versions)

## Out of scope

Please don't report:

- **Self-signed certificate trust warnings** — Stampd ships a self-signed demo cert. Adopters bring their own AATL-trusted cert for production. The demo flow's "Adobe shows yellow badge" is documented behavior, not a vulnerability.
- **Issues that require local-machine access** to the user running Stampd (e.g., reading `appsettings.json` if you already have the host's filesystem).
- **Denial of service via legitimate request volume** — protect with upstream rate limiting (Stampd has request-throttling middleware, but it's not a replacement for a WAF).
- **Missing security headers on the demo `Stampd.UI`** — the UI is a reference signer experience, not a hardened production frontend. Adopters embedding the signer flow should add CSP / HSTS / X-Frame-Options at their host layer.
- **Known limitations** documented in `internal/implementation/*.md` (e.g., the SqliteStampdDbContext `Id-as-Modified` gotcha, the EF Core `OrderBy DateTimeOffset?` limitation routed through epoch proxy column).
- **Social engineering attacks** against signers (legitimate signer choosing to share their access token with another party).

## Security practices in the project

For adopters evaluating Stampd's security posture:

- **GitHub Advanced Security**: CodeQL static analysis runs on every push to `main`. Latest results: <https://github.com/Nerttiyana-Technologies/Stampd/security/code-scanning>
- **Secret scanning**: GitHub secret scanning + push protection enabled. Zero open alerts at `main`.
- **Dependency scanning**: Dependabot configured for all NuGet packages with automated PRs for CVE patches.
- **Cryptographic primitives**: BouncyCastle.NET 2.6.2 for CMS / ASN.1, .NET 10 `System.Security.Cryptography` for hashing + signing, no homebrew crypto.
- **HSM-first design**: production deployments target Vault, OpenBao, Azure Key Vault, or AWS KMS. Private keys never leave the HSM.
- **Append-only audit log**: every state transition writes an `AuditEvent` with actor attribution (v2.0+) and timestamp. Not modifiable via API.
- **Test coverage**: PAdES regression suite (`PadesBLtaTests`, `PadesDocTimeStampTests`, `PadesIncrementalUpdateTests`) verifies signature byte ranges and ATSHashIndexV3 computation against ETSI reference vectors.

## Acknowledgements

Researchers who have responsibly disclosed vulnerabilities will be credited here (unless they request anonymity). To date: (none yet — Stampd is pre-1.0 of public adoption).

---

*Last updated: 2026-06-07. This policy may change as Stampd's adoption and threat model evolve — check the version in the repo for the latest.*
