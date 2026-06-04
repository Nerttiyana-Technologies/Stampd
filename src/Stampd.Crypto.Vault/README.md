# Stampd.Crypto.Vault

HashiCorp Vault / OpenBao Transit-backed ICryptographicSealingProvider. The signing key stays in Vault; this provider submits data to the Transit secrets engine for remote signing. Works against both HashiCorp Vault (BUSL-1.1) and OpenBao (MPL-2.0) via their shared HTTP API.

Part of [Stampd](https://github.com/isureshsubramanian/Stampd) — open-source PDF signing for .NET. Apache 2.0.

## Install

```
dotnet add package Stampd.Crypto.Vault
```

See the [Stampd repository](https://github.com/isureshsubramanian/Stampd) for the full architecture, getting-started guide, and the rest of the Stampd packages.
