# Bring Your Own Certificate — Deployment Guide

Stampd signs PDFs using **your** customer's certificate. The customer decides where their private key lives; Stampd reads it through one of four pluggable sealing providers. This guide covers every realistic deployment shape, including hard constraints like "no Docker" or "no Vault."

---

## Quick decision tree

```
                  Customer's signing key lives in...
                              │
        ┌─────────────────────┼──────────────────────┐
        │                     │                      │
   A .pfx file           A cloud HSM             A self-hosted
   (or .p12 / .crt+.key)  (Azure KV / AWS KMS)   HSM (Vault / OpenBao)
        │                     │                      │
        ▼                     ▼                      ▼
   LocalCertificate     AzureKeyVault            Vault
     provider           or AwsKms provider       provider
```

If you don't know yet which the customer has, **start with LocalCertificate** — every customer can produce a PFX, even if they later migrate to an HSM. The provider is a config flip, not a code change.

---

## Path A — LocalCertificate (no Docker, no Vault required)

The simplest production setup. Works on bare metal, in VMs, on Windows IIS, on Linux systemd, in containers — anything that runs a .NET process. Customer doesn't need new infrastructure.

### What the customer gives you

One of these three:

- A **`.pfx`** (or `.p12`) file — combined certificate + private key, password-protected. Most common output from CAs.
- A **`.cer` + `.key`** pair — separate files. Convert them to PFX with `openssl pkcs12 -export -in cert.cer -inkey private.key -out signer.pfx`.
- An **OS-installed certificate** — already in Windows Certificate Store (`LocalMachine\My`) or Linux NSS. (See "OS keychain" subsection below.)

### Deploy the PFX

Don't put the PFX in the repo, don't put the password in `appsettings.json`. Pick one of these mount strategies:

#### Linux + systemd (recommended for "no Docker" customers)

```bash
# 1. Place the PFX in a restricted directory owned by the stampd service account
sudo mkdir -p /etc/stampd
sudo cp customer-signer.pfx /etc/stampd/signer.pfx
sudo chown stampd:stampd /etc/stampd/signer.pfx
sudo chmod 400 /etc/stampd/signer.pfx     # only owner can read

# 2. Pass the PFX password as a systemd credential (encrypted on disk)
sudo systemd-creds encrypt --name=stampd-pfx-pass - /etc/stampd/pfx-pass.cred <<< "<the-pfx-password>"

# 3. Reference both in your unit file
cat > /etc/systemd/system/stampd-webapi.service <<'EOF'
[Unit]
Description=Stampd WebApi
After=network.target

[Service]
Type=notify
User=stampd
Group=stampd
LoadCredentialEncrypted=stampd-pfx-pass:/etc/stampd/pfx-pass.cred
Environment="Stampd__Sealing__Provider=LocalCertificate"
Environment="Stampd__Sealing__LocalCertificate__PfxPath=/etc/stampd/signer.pfx"
ExecStart=/usr/bin/dotnet /opt/stampd/Stampd.WebApi.dll
Restart=on-failure

[Service]
# Pull the credential into an environment variable the .NET host reads
ExecStartPre=/bin/bash -c 'export Stampd__Sealing__LocalCertificate__PfxPassword=$(cat $CREDENTIALS_DIRECTORY/stampd-pfx-pass)'

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now stampd-webapi
```

The password lives only in kernel-encrypted form on disk; the .NET process reads it via a transient env var. No Docker, no Vault, no plaintext password anywhere on the filesystem.

#### Windows + IIS

Put the PFX in the **Local Machine** certificate store and reference it by thumbprint — Windows DPAPI manages the password internally. No PFX file on disk after import.

```powershell
# 1. Import the PFX into Local Machine\My (run as Administrator)
$pwd = ConvertTo-SecureString "<the-pfx-password>" -AsPlainText -Force
Import-PfxCertificate -FilePath C:\customer-signer.pfx -CertStoreLocation Cert:\LocalMachine\My -Password $pwd

# 2. Get the thumbprint
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*Customer Signer*" } | Select-Object Thumbprint
# Copy the thumbprint string

# 3. Grant the app pool identity read access to the private key
$cert = Get-ChildItem "Cert:\LocalMachine\My\<thumbprint>"
$rsaKey = $cert.PrivateKey
$keyPath = "$env:ALLUSERSPROFILE\Microsoft\Crypto\RSA\MachineKeys\$($rsaKey.CspKeyContainerInfo.UniqueKeyContainerName)"
icacls $keyPath /grant "IIS AppPool\StampdWebApi:R"

# 4. Securely delete the source PFX file
sdelete -p 7 C:\customer-signer.pfx
```

Then in `appsettings.Production.json`:

```jsonc
{
  "Stampd": {
    "Sealing": {
      "Provider": "LocalCertificate",
      "LocalCertificate": {
        // OS keychain mode — no PFX file path, no password handling needed
        "Thumbprint": "<paste-thumbprint-here>",
        "StoreLocation": "LocalMachine",
        "StoreName": "My"
      }
    }
  }
}
```

#### Container (Docker, Kubernetes, etc.) — if customer is fine with one container

Mount the PFX as a read-only secret volume, pass the password via env var sourced from a secret store.

```yaml
# k8s deployment.yaml fragment
spec:
  containers:
    - name: stampd-webapi
      env:
        - name: Stampd__Sealing__Provider
          value: LocalCertificate
        - name: Stampd__Sealing__LocalCertificate__PfxPath
          value: /secrets/signer.pfx
        - name: Stampd__Sealing__LocalCertificate__PfxPassword
          valueFrom:
            secretKeyRef:
              name: stampd-pfx-pass
              key: password
      volumeMounts:
        - name: signer-pfx
          mountPath: /secrets
          readOnly: true
  volumes:
    - name: signer-pfx
      secret:
        secretName: stampd-pfx
        defaultMode: 0400
```

### Configuration reference for LocalCertificate

```jsonc
{
  "Stampd": {
    "Sealing": {
      "Provider": "LocalCertificate",
      "LocalCertificate": {
        // Pick ONE of the two modes below:

        // Mode 1 — PFX file on disk
        "PfxPath": "/etc/stampd/signer.pfx",
        "PfxPassword": "<set-via-env-var-not-here>",

        // Mode 2 — Windows certificate store (Linux/macOS too, via .NET cert store APIs)
        "Thumbprint": "ABCDEF1234...",
        "StoreLocation": "LocalMachine",    // or "CurrentUser"
        "StoreName": "My"                    // standard "personal" store
      }
    }
  }
}
```

---

## Path B — Azure Key Vault (no infra to run, customer is on Azure)

If the customer is on Azure but won't deploy Docker or Vault, Azure Key Vault is the cleanest HSM option. It's a fully-managed Azure service — there's nothing to install or maintain.

### Setup (customer's Azure subscription)

1. Customer creates a Key Vault and imports their certificate (or asks DigiCert/GlobalSign to provision directly into the vault).
2. Customer grants your VM / App Service's **managed identity** the `Key Vault Crypto User` role on the vault.
3. Stampd uses `DefaultAzureCredential` to authenticate — no secrets in config.

### Configuration

```jsonc
{
  "Stampd": {
    "Sealing": {
      "Provider": "AzureKeyVault",
      "AzureKeyVault": {
        "VaultUri": "https://customer-stampd-vault.vault.azure.net/",
        "CertificateName": "signer-cert",
        "KeyName": "signer-key"
      }
    }
  }
}
```

No password, no PFX on disk. The signing call goes to Key Vault; the private key never leaves Azure's HSM.

---

## Path C — AWS KMS (no infra to run, customer is on AWS)

Same pattern as Azure, on AWS. Customer creates an asymmetric KMS key, grants the EC2 instance role / IAM user `kms:Sign` permission.

```jsonc
{
  "Stampd": {
    "Sealing": {
      "Provider": "AwsKms",
      "AwsKms": {
        "KeyId": "arn:aws:kms:us-east-1:123456789012:key/12345678-...",
        "Region": "us-east-1",
        "CertificatePath": "/etc/stampd/signer.cer"   // public cert only, not the key
      }
    }
  }
}
```

AWS doesn't store the public cert alongside the KMS key, so you still mount the `.cer` file. The private key stays inside KMS.

---

## Path D — Self-hosted Vault / OpenBao (if customer is fine with it)

Skipped here — see the existing `internal/implementation/` Vault docs.

---

## "Customer rejects Docker AND Vault" — risk discussion

This is the most common constraint at small-to-mid businesses, regulated industries with strict change control, or air-gapped environments. Here's how to talk through it:

### What they're really saying

"Docker" usually means *"no new runtime to learn, no orchestrator, no daemon I have to monitor."* "Vault" means *"no separate piece of infrastructure I have to operate, rotate tokens for, or audit."*

These are operational concerns, not security concerns. The mitigation isn't to argue back — it's to show them an architecture that respects the constraint while preserving security.

### Path that satisfies "no Docker, no Vault"

**Option 1 — LocalCertificate + OS keychain.** Stampd runs as a normal .NET process (systemd unit on Linux, Windows Service or IIS app pool on Windows). The PFX is imported into the OS certificate store (or stored as a permission-locked file). No containers, no extra daemons.

- ✅ Familiar deployment shape (systemd / IIS — every ops team knows these)
- ✅ No new daemons to operate
- ✅ Key access is gated by OS user / group permissions — auditable via existing OS tooling
- ⚠️ Private key lives in plaintext on disk (encrypted at rest if the FS is encrypted, but in memory during signing)
- ⚠️ Key rotation is a manual file swap
- ⚠️ If the host is compromised, the key is exfiltratable

**Option 2 — LocalCertificate + customer's existing secret manager.** If the customer uses AWS Secrets Manager / Azure Key Vault / HashiCorp's hosted offering / 1Password Connect / etc., point the password fetch at that service. The PFX itself can still be a file; the password is pulled at process startup.

- ✅ No new infrastructure (uses what they already have)
- ✅ Password never touches the filesystem
- ⚠️ Still a PFX file on disk during runtime

**Option 3 — Cloud HSM (Azure KV / AWS KMS).** If the customer is on a cloud, the cloud's native KMS is *not* "new infrastructure" — it's a managed service they're already paying for. This is usually the best fit for compliance-heavy customers.

- ✅ Private key never leaves the cloud HSM (NIST/FIPS-validated)
- ✅ Signing audit trail is built into the cloud audit log
- ✅ No new servers, no Vault daemon, no Docker
- ⚠️ Per-signature cost (typically $0.03/signature on Azure KV, lower on AWS)

### How to recommend

Walk through this decision tree with the customer in plain language:

1. *"Do you use Azure or AWS? Y → Path B or C — no new infra."*
2. *"Do you have an existing enterprise secret manager? Y → Path A with the password fetched from it."*
3. *"Are you OK with a PFX file on a permission-locked server? Y → Path A, plain."*
4. *"Are you NONE of the above and you don't trust file-based secrets? You need an HSM — let's talk about whether you'd accept managed cloud HSM or a SmartCard / YubiKey."*

That last branch is rare but real (defense / financial regulation). For those customers, Stampd would need a PKCS#11 provider — currently on the v3.0 roadmap.

### The risk you should disclose

Whichever path the customer picks, document the residual risk:

| Path | Key exposure | Rotation effort | Audit trail |
|---|---|---|---|
| LocalCertificate (PFX on disk) | Anyone with root on the host | Manual file swap + service restart | Host's syslog + Stampd audit log |
| LocalCertificate (OS keychain) | Anyone with Administrator + key-storage permission | OS-level cert import | Windows event log + Stampd audit log |
| Azure Key Vault | No one — key never leaves vault | Roll cert in vault, Stampd picks up via name | Azure audit log + Stampd audit log |
| AWS KMS | No one — key never leaves KMS | Create new key version | CloudTrail + Stampd audit log |
| Self-hosted Vault | Customer's Vault operators | `vault write` to rotate | Vault audit device + Stampd audit log |

For a regulated customer, the disclosure should be in writing. For a small-business customer, it can be a brief conversation.

---

## Cert rotation

All providers support hot rotation — Stampd reads the cert/key on each signing request, not at startup. To rotate:

- **LocalCertificate (file)**: replace `signer.pfx` on disk. Update password if it changed. No restart needed (subject to file-handle caching — restart if signatures start failing).
- **LocalCertificate (OS store)**: import the new cert, get the new thumbprint, update `appsettings.json`, restart.
- **Azure Key Vault / AWS KMS**: rotate in the cloud console. Stampd uses the latest version automatically.
- **Vault**: `vault write transit/keys/<name>/rotate`. New signatures use the new key version; old signatures remain verifiable against the old version.

**Important**: never delete the old cert/key. PAdES B-LT and B-LTA signatures need the original signing cert to verify their embedded validation data for the lifetime of the document.

---

## Verifying in Adobe

For the green "Validity is unknown but signature is valid" → green "Signature is valid" upgrade, the customer's cert must be:

- **AATL-trusted** — issued by a CA on the Adobe Approved Trust List (DigiCert, GlobalSign, Entrust, Sectigo, IdenTrust, and ~40 others), OR
- **Manually trusted** — recipient drags the `.cer` into Adobe's "Trusted Identities" once.

A self-signed cert will always show "signer identity unknown" with a yellow badge. Customers in production almost always have an AATL cert. Customers in evaluation often don't and accept the yellow badge.

If the customer doesn't have an AATL cert, point them at:

- DigiCert Document Signing — ~$300/year, certificate lives in DigiCert's cloud HSM
- GlobalSign DSS — pay-per-signature, similar
- Or a YubiKey + Sectigo cert if they want hardware-token signing

---

## TL;DR

| Customer constraint | Provider | What you deploy |
|---|---|---|
| No constraints, fastest setup | `LocalCertificate` (PFX file) | A `.pfx` + a path + a password in env |
| No Docker, no Vault, on Linux | `LocalCertificate` + systemd creds | `.pfx` file + systemd unit |
| No Docker, no Vault, on Windows | `LocalCertificate` (cert store) | Import to `LocalMachine\My`, reference by thumbprint |
| On Azure, compliance-heavy | `AzureKeyVault` | Vault URI + cert name + managed identity |
| On AWS, compliance-heavy | `AwsKms` | KMS key ARN + public `.cer` path |
| Customer has Vault / OpenBao | `Vault` | Vault URL + transit key name + AppRole auth |

Whatever path: the private key never goes in `appsettings.json`, secret material flows through environment variables or platform secret stores, and rotation is hot.
