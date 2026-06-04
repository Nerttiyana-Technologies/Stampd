# Stampd.Crypto.AwsKms

AWS KMS-backed ICryptographicSealingProvider. The signing key stays in AWS KMS; this provider submits digests for remote signing via the KMS Sign API. Pairs with AWS-issued AATL-trusted certificates or customer-supplied certs whose public key matches the KMS asymmetric key.

Part of [Stampd](https://github.com/isureshsubramanian/Stampd) — open-source PDF signing for .NET. Apache 2.0.

## Install

```
dotnet add package Stampd.Crypto.AwsKms
```

See the [Stampd repository](https://github.com/isureshsubramanian/Stampd) for the full architecture, getting-started guide, and the rest of the Stampd packages.
