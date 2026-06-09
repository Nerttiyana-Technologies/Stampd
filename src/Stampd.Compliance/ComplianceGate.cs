namespace Stampd.Compliance;

/// <summary>
/// v3.0.0 — default <see cref="IComplianceGate"/> implementation. Validates the
/// three rules every bundle enforces: signature level meets the bundle's floor,
/// identity verification is present when required, and the sealing provider
/// holds a QES certification when the bundle demands one.
/// </summary>
public sealed class ComplianceGate : IComplianceGate
{
    public void Validate(ComplianceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (declaration.Bundle == ComplianceBundle.None) return;

        // Rule 1: signature level floor.
        var requiredLevel = ComplianceLevels.MinimumSignatureLevel(declaration.Bundle);
        if (!IsAtLeast(declaration.SignatureLevel, requiredLevel))
        {
            throw new ComplianceViolationException(
                $"{declaration.Bundle} requires minimum signature level {requiredLevel}; " +
                $"request declared {declaration.SignatureLevel}.");
        }

        // Rule 2: identity verification.
        if (ComplianceLevels.RequiresIdentityVerification(declaration.Bundle)
            && !declaration.RequiresIdentityVerification)
        {
            throw new ComplianceViolationException(
                $"{declaration.Bundle} requires identity verification on every recipient; " +
                "request did not enable IV.");
        }

        // Rule 3: QES cert (eIDAS only).
        if (declaration.Bundle == ComplianceBundle.EidasQes
            && !declaration.SealingProviderIsQesCertified)
        {
            throw new ComplianceViolationException(
                "EidasQes requires a QES-certified sealing provider (ISignsAtQesLevel). " +
                "The registered provider does not advertise QES certification.");
        }
    }

    /// <summary>
    /// Ordered comparison on the PAdES level ladder. B-B &lt; B-T &lt; B-LT &lt; B-LTA.
    /// Returns true iff <paramref name="actual"/> is at or above <paramref name="floor"/>.
    /// </summary>
    private static bool IsAtLeast(string actual, string floor)
    {
        var actualRank = Rank(actual);
        var floorRank = Rank(floor);
        return actualRank >= floorRank;
    }

    private static int Rank(string level) => level switch
    {
        ComplianceLevels.PadesBb => 0,
        ComplianceLevels.PadesBt => 1,
        ComplianceLevels.PadesBlt => 2,
        ComplianceLevels.PadesBlta => 3,
        _ => -1, // unknown levels rank lowest — they fail any floor check
    };
}
