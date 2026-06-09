namespace Stampd.Compliance;

/// <summary>
/// v3.0.0 — industry compliance preset selector. Set on a per-signing-request
/// basis via <c>SigningRequest.PayloadJson</c> metadata (no schema change in v3.0);
/// v3.1 promotes it to a typed column with a V17 migration.
/// </summary>
public enum ComplianceBundle
{
    /// <summary>No bundle. v2.x default behavior — adopter picks signature level + IV gates manually.</summary>
    None = 0,

    /// <summary>
    /// HIPAA Title II — Security Rule. 6-year audit retention minimum, PHI
    /// redaction on GDPR-style erase, B-LT minimum signature level, identity
    /// verification required for every recipient.
    /// </summary>
    Hipaa = 1,

    /// <summary>
    /// FDA 21 CFR Part 11 — electronic records / electronic signatures.
    /// B-LTA minimum signature level, manifest required, time-zoned timestamps,
    /// audit trail metadata embedded in the signed PDF.
    /// </summary>
    Cfr21Part11 = 2,

    /// <summary>
    /// eIDAS QES — Qualified Electronic Signature. Requires a QES-certified
    /// signing cert (sealing provider must implement <see cref="ISignsAtQesLevel"/>),
    /// strict EU-listed TSA, no override possible per-request.
    /// </summary>
    EidasQes = 3,
}

/// <summary>
/// Sealing-provider marker — implemented by sealing providers that hold a
/// QES-certified key (sealed by an EU-Trusted-List CA). The compliance gate
/// checks this on every EidasQes dispatch.
/// </summary>
public interface ISignsAtQesLevel
{
}

/// <summary>
/// Minimum signature levels required by each bundle, mirrored against
/// Stampd's PAdES level vocabulary.
/// </summary>
public static class ComplianceLevels
{
    public const string PadesBb = "B-B";
    public const string PadesBt = "B-T";
    public const string PadesBlt = "B-LT";
    public const string PadesBlta = "B-LTA";

    /// <summary>Lookup: bundle → minimum signature level.</summary>
    public static string MinimumSignatureLevel(ComplianceBundle bundle) => bundle switch
    {
        ComplianceBundle.Hipaa => PadesBlt,
        ComplianceBundle.Cfr21Part11 => PadesBlta,
        ComplianceBundle.EidasQes => PadesBlta,
        _ => PadesBb,
    };

    /// <summary>Lookup: bundle → audit retention years (used by the maintenance worker, v3.1).</summary>
    public static int AuditRetentionYears(ComplianceBundle bundle) => bundle switch
    {
        ComplianceBundle.Hipaa => 6,
        ComplianceBundle.Cfr21Part11 => 7,
        ComplianceBundle.EidasQes => 10,
        _ => 0, // unbounded by default
    };

    /// <summary>Lookup: bundle → identity verification required.</summary>
    public static bool RequiresIdentityVerification(ComplianceBundle bundle) => bundle switch
    {
        ComplianceBundle.Hipaa => true,
        ComplianceBundle.Cfr21Part11 => true,
        ComplianceBundle.EidasQes => true,
        _ => false,
    };
}
