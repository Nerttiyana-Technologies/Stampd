# stampd-certcheck

Deployment validation + customer demo tool. Takes any customer's PFX file, runs it through Stampd's signing pipeline, and prints a structured pass/fail report in about 4 seconds.

## Why it exists

Two audiences:

1. **Adopter deploying Stampd at a customer site** — run this once against the customer's PFX *before* wiring it into the WebApi. Catches expired certs, wrong passwords, missing private keys, weak key sizes, and unsupported key algorithms in a single command.
2. **Stampd team in a customer demo** — "hand me your PFX, watch this take 4 seconds." The command output reads naturally on a screen: cert metadata, signed sample bytes, structural verification, signed PDF written to disk for live Adobe inspection.

This is intentionally separate from `samples/Stampd.Engine.Smoke` — that one is an internal engine spike test, this one is the customer-facing deployment validator.

## Run it

```bash
# Customer hands you a PFX
dotnet run --project samples/Stampd.CertCheck -- \
  --pfx ./customer-signer.pfx --password "$PFX_PASSWORD"

# No customer cert yet? Demo with a fresh self-signed cert
dotnet run --project samples/Stampd.CertCheck -- --generate-self-signed

# Add an RFC 3161 timestamp for a PAdES B-T proof (1 extra TSA round-trip)
dotnet run --project samples/Stampd.CertCheck -- \
  --pfx ./customer-signer.pfx --password "$PFX_PASSWORD" --tsa
```

## What you get

```
  stampd-certcheck
  Deployment validation for Stampd's bring-your-own-cert flow

→ [1/4] Loading certificate
  ✓ Provider initialised: LocalCertificate  (47 ms)
     Subject:        CN=Acme Demo Signer, O=Acme Corp, C=US
     Issuer:         CN=DigiCert Document Signing CA, O=DigiCert, C=US
     Serial:         01ABCDEF1234567890
     Thumbprint:     A1B2C3D4...
     Valid from:     2025-06-01
     Valid to:       2026-06-01
     Key algorithm:  RSA-3072
     Has private key: True
     Self-signed:    No (cert chains to an issuing CA)

→ [2/4] Signing synthetic PDF
  ✓ Signed 1,847 B → 12,403 B (+10,556 B added)  (1,203 ms)
     Profile:        PAdES B-T (with RFC 3161 timestamp)
     SHA-256:        4f8b...
     Signed at UTC:  2026-06-07 12:34:56Z

→ [3/4] Verifying signed output structure
  ✓ All structural checks passed  (3 ms)
     ✓ PDF magic bytes
     ✓ Trailing %%EOF
     ✓ Contains /Sig dict
     ✓ Contains /Contents
     ✓ Contains /ByteRange
     ✓ SubFilter present

→ [4/4] Writing signed sample for review
  ✓ Sample written to /current/dir/certcheck-output.pdf  (5 ms)

╔══════════════════════════════════════════════════════════════════════╗
║  ✓  CERTIFICATE VALIDATION PASSED                                    ║
║                                                                      ║
║  Subject:        CN=Acme Demo Signer, O=Acme Corp, C=US              ║
║  Days to expiry: 359                                                 ║
║  Profile:        PAdES B-T (with RFC 3161 timestamp)                 ║
║  Sample output:  /current/dir/certcheck-output.pdf                   ║
╚══════════════════════════════════════════════════════════════════════╝
```

Then open `certcheck-output.pdf` in Adobe Acrobat: the signature panel populates, the byte-range verifies, and you get the same green/yellow check the production WebApi would produce.

## Demo script (customer-facing)

1. *"Send me your signing PFX — the same one you'd use in production."*
2. Save it as `./customer.pfx`. Run:
   ```bash
   dotnet run --project samples/Stampd.CertCheck -- --pfx ./customer.pfx --password "$PFX_PASS"
   ```
3. Walk through the structured output line by line — *"these are the checks Stampd runs internally on every signature."*
4. Open `certcheck-output.pdf` in Adobe Acrobat on the demo laptop. Show the signature panel. If the cert is AATL-trusted, point at the green check. If it's not, show the yellow badge, then drag the customer's `.cer` into Adobe's Trusted Certificates to flip it green — *"this is what every customer's recipient sees once you've configured AATL trust at your endpoint."*
5. *"The same provider config — `LocalCertificate` with `PfxPath` and `PfxPassword` — is what you'd put in `appsettings.Production.json`. Want me to walk through the deployment doc?"*

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Certificate ready for production |
| `1` | Unexpected failure (wrong password, corrupted PFX, etc.) |
| `2` | Missing or invalid CLI arguments |
| `3` | Certificate expired |
| `4` | Signed PDF failed structural verification (Stampd engine bug — report as an issue) |

Use these exit codes in deployment scripts to gate WebApi startup behind a successful certcheck run.

## What's not covered

This tool currently exercises only the **LocalCertificate** sealing provider. The other three providers (Vault, AzureKeyVault, AwsKms) follow the same `ICryptographicSealingProvider` interface — adding them to the CLI is purely a wiring exercise. Targeted for a follow-up if there's adopter demand.

For HSM-backed deployment validation today, write a 20-line wrapper that registers the relevant provider via DI and call `engine.SignAsync` the same way this tool does. The signing pipeline is provider-agnostic.

## Related docs

- `docs/deployment-bring-your-own-cert.md` — full deployment guide for all four providers
- `SECURITY.md` — vulnerability disclosure + scope
- `samples/Stampd.Engine.Smoke/` — internal engine smoke test (different audience, do not modify)
