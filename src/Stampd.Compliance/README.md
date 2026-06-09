# Stampd.Compliance

v3.0.0 — industry compliance bundles for Stampd. Runtime-enforced gates on signature level, identity verification, and QES certification.

## Bundles

| Bundle | Minimum signature | Identity verification | Audit retention | Notes |
|---|---|---|---|---|
| `Hipaa` | B-LT | Required | 6 years | Title II Security Rule. PHI redaction on GDPR erase. |
| `Cfr21Part11` | B-LTA | Required | 7 years | FDA electronic records. Time-zoned timestamps. |
| `EidasQes` | B-LTA | Required | 10 years | Qualified Electronic Signature. EU-listed TSA + QES cert. |
| `None` | B-B (default) | Optional | Unbounded | v2.x default behavior. |

## Usage

```csharp
services.AddStampdCompliance();

// In your dispatch path:
var gate = serviceProvider.GetRequiredService<IComplianceGate>();
gate.Validate(new ComplianceDeclaration(
    Bundle: ComplianceBundle.Hipaa,
    SignatureLevel: "B-LT",
    RequiresIdentityVerification: true,
    SealingProviderIsQesCertified: false));
// throws ComplianceViolationException if any rule fails
```

## What's NOT yet wired in v3.0.0

- Persistent `SigningRequest.ComplianceBundle` column. v3.0 adopters set the bundle as part of the dispatch request metadata; v3.1 adds a typed column + V17 migration.
- Audit retention enforcement in `MaintenanceWorker`. Bundle constants define the years; the worker that prunes audit rows lands in v3.1.
- PHI redaction (HIPAA). Stampd's existing GDPR-erase flow handles most cases; HIPAA-specific PHI classification is a future enhancement.
