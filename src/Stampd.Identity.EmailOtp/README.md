# Stampd.Identity.EmailOtp

Email one-time-password IIdentityVerificationProvider. Generates a 6-digit code, emails it via IEmailSender, verifies on submission. The default in-memory challenge store is for dev/single-node; production hosts should swap in a DB-backed IOtpChallengeStore.

Part of [Stampd](https://github.com/isureshsubramanian/Stampd) — open-source PDF signing for .NET. Apache 2.0.

## Install

```
dotnet add package Stampd.Identity.EmailOtp
```

See the [Stampd repository](https://github.com/isureshsubramanian/Stampd) for the full architecture, getting-started guide, and the rest of the Stampd packages.
