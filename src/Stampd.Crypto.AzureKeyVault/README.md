# Stampd.Crypto.AzureKeyVault

Azure Key Vault HSM-backed ICryptographicSealingProvider. The signing key stays in the Key Vault HSM; this provider hashes locally and submits the digest for remote signing.

Part of [Stampd](https://github.com/isureshsubramanian/Stampd) — open-source PDF signing for .NET. Apache 2.0.

## Install

```
dotnet add package Stampd.Crypto.AzureKeyVault
```

See the [Stampd repository](https://github.com/isureshsubramanian/Stampd) for the full architecture, getting-started guide, and the rest of the Stampd packages.
