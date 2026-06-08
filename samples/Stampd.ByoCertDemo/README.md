# Stampd ByoCertDemo

Customer-facing **bring-your-own-cert** demo. Self-contained Blazor Server app — separate from `Stampd.UI` and `Stampd.WebApi`. Built for the meeting where the customer says *"we have our own signing certificate, show me how this works."*

## What the customer sees

1. **`/` — the vault**: list of every certificate uploaded so far, with subject, expiry, key algorithm, trust badge. Each card has a **"Sign a PDF →"** button.
2. **Upload flow** (inline expand on the vault page): PFX file picker, password, optional friendly name. One click validates the cert + encrypts + persists it. Wrong passwords surface as a clean inline error.
3. **`/sign/{id}` — the signing detail**: cert metadata at the top, three steps below (pick PDF, pick profile B-B or B-T, click Sign). Result panel shows wall-time, document hash, profile chosen, signed-PDF size delta. **Download** and **Open in browser** buttons. The inline viewer renders the signed PDF via a `data:` URL so there's no temporary file on the server.

## How storage works

Encrypted at rest using ASP.NET Core's **Data Protection** API. Vault layout:

```
~/.stampd/byo-cert-demo/
├── keys/                   # key ring (auto-managed, AES-256-GCM)
└── vault/
    ├── {guid}.meta         # plaintext metadata — subject, issuer, expiry, etc.
    └── {guid}.cert         # encrypted blob containing PFX bytes + password
```

The plaintext metadata is safe to leave in the clear — no key material. The `.cert` blob is encrypted with a managed key under `keys/` and contains both the PFX bytes and the PFX password, so the only thing the demoer has to remember is the friendly name they typed in.

**Override the path** with `Stampd:ByoCertDemo:VaultPath` in `appsettings.json` or via env var if you want vault state outside your home directory.

## Run it

```bash
dotnet run --project samples/Stampd.ByoCertDemo
```

Browser opens at <http://localhost:5180>. First load shows an empty vault.

### No PFX handy? Generate one for the demo:

```bash
openssl req -x509 -newkey rsa:3072 -keyout signer.key -out signer.cer \
  -sha256 -days 365 -nodes \
  -subj "/CN=Demo Signer/O=Demo/C=US" \
  -addext "keyUsage=digitalSignature,nonRepudiation" \
  -addext "extendedKeyUsage=emailProtection,1.3.6.1.4.1.311.10.3.12"

openssl pkcs12 -export -inkey signer.key -in signer.cer -out signer.pfx \
  -password pass:demo
```

Upload `signer.pfx` with password `demo`. Self-signed certs show a yellow badge in Adobe — drag the `.cer` into Adobe's Trusted Certificates once and signatures flip to green.

## Demo script (script it once, repeat at every customer)

1. **Open the vault page**. Point at the description: *"Your cert never leaves this machine. We store the PFX encrypted at rest using AES-256-GCM with a managed key ring."*
2. **Click "+ Upload a new certificate"**. Pick the customer's PFX. Enter the password. Click **Save to vault**. Wait for the card to appear in the grid.
3. *"That just validated your cert end-to-end — loaded the private key, extracted the public cert, and stored it encrypted on disk. Watch what happens if I give it a bad password..."* (demonstrate the error inline).
4. **Click "Sign a PDF →" on the card**. Upload one of the customer's actual PDFs. Choose **PAdES B-T**. Click **Sign now**.
5. *"That just took 1.3 seconds. Round-trip to a public timestamp authority included. Here's the result..."* — click **Open in browser**, the signed PDF renders inline.
6. **Click Download**. Open in Adobe Acrobat on the demo laptop. Show the signature panel: subject, time, timestamp authority, "Signature is valid."

That's the demo. Roughly 3 minutes for the full flow.

## What this is NOT

- Not a multi-tenant production app — single-process, single-user vault.
- Not HSM-backed — the master key lives on the local filesystem under `keys/`. For production, route the same `IDataProtector` calls through Vault / Azure Key Vault / AWS KMS via the standard Data Protection providers.
- Not the `Stampd.UI` / `Stampd.WebApi` flow — those are still where you'd build the production signer workflow. This is a focused, single-purpose demo for the bring-your-own-cert conversation.

## Related

- `docs/deployment-bring-your-own-cert.md` — full production guide for the four sealing providers (LocalCertificate, Vault, AzureKeyVault, AwsKms)
- `samples/Stampd.CertCheck/` — CLI sibling of this tool for deployment validation
- `SECURITY.md` — vulnerability disclosure + scope
