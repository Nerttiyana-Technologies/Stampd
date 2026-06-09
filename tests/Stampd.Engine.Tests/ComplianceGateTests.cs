using Stampd.Compliance;

using Xunit;

namespace Stampd.Engine.Tests;

/// <summary>
/// v3.0.0 — runtime tests for the compliance gate. One test per rule so
/// failures point directly at the violated constraint.
/// </summary>
public sealed class ComplianceGateTests
{
    private static readonly ComplianceGate Gate = new();

    [Fact]
    public void None_AcceptsAnything()
    {
        // Bundle = None is the v2.x escape hatch; the gate must never throw.
        var d = new ComplianceDeclaration(
            Bundle: ComplianceBundle.None,
            SignatureLevel: ComplianceLevels.PadesBb,
            RequiresIdentityVerification: false,
            SealingProviderIsQesCertified: false);

        Gate.Validate(d); // no throw
    }

    [Fact]
    public void Hipaa_AcceptsBlt_WithIv()
    {
        var d = new ComplianceDeclaration(
            Bundle: ComplianceBundle.Hipaa,
            SignatureLevel: ComplianceLevels.PadesBlt,
            RequiresIdentityVerification: true,
            SealingProviderIsQesCertified: false);

        Gate.Validate(d);
    }

    [Fact]
    public void Hipaa_RejectsBb_BelowSignatureFloor()
    {
        var d = new ComplianceDeclaration(
            Bundle: ComplianceBundle.Hipaa,
            SignatureLevel: ComplianceLevels.PadesBb,
            RequiresIdentityVerification: true,
            SealingProviderIsQesCertified: false);

        var ex = Assert.Throws<ComplianceViolationException>(() => Gate.Validate(d));
        Assert.Contains("B-LT", ex.Message);
    }

    [Fact]
    public void Hipaa_RejectsMissingIv()
    {
        var d = new ComplianceDeclaration(
            Bundle: ComplianceBundle.Hipaa,
            SignatureLevel: ComplianceLevels.PadesBlt,
            RequiresIdentityVerification: false,
            SealingProviderIsQesCertified: false);

        var ex = Assert.Throws<ComplianceViolationException>(() => Gate.Validate(d));
        Assert.Contains("identity verification", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EidasQes_RejectsNonQesSealingProvider()
    {
        var d = new ComplianceDeclaration(
            Bundle: ComplianceBundle.EidasQes,
            SignatureLevel: ComplianceLevels.PadesBlta,
            RequiresIdentityVerification: true,
            SealingProviderIsQesCertified: false);

        var ex = Assert.Throws<ComplianceViolationException>(() => Gate.Validate(d));
        Assert.Contains("QES", ex.Message);
    }
}
