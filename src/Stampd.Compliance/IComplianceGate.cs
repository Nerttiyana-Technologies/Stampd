namespace Stampd.Compliance;

/// <summary>
/// v3.0.0 — runtime gate that validates a request against its declared
/// <see cref="ComplianceBundle"/>. Called from <c>SigningWorkflowService.SubmitAsync</c>
/// at dispatch time; throws <see cref="ComplianceViolationException"/> when the
/// request doesn't meet the bundle's requirements.
/// </summary>
public interface IComplianceGate
{
    /// <summary>
    /// Validate that <paramref name="declaration"/> can be dispatched under the
    /// configured bundle. Returns silently on success; throws on violation.
    /// </summary>
    /// <exception cref="ComplianceViolationException">
    /// The declaration does not meet the bundle's floor (signature level too low,
    /// IV missing when required, sealing provider lacking QES certification).
    /// </exception>
    void Validate(ComplianceDeclaration declaration);
}

/// <summary>
/// Inputs the gate validates against. Construct from the dispatch path's
/// <c>SigningRequest</c> + the registered <c>ICryptographicSealingProvider</c>.
/// </summary>
public sealed record ComplianceDeclaration(
    ComplianceBundle Bundle,
    string SignatureLevel,
    bool RequiresIdentityVerification,
    bool SealingProviderIsQesCertified);

/// <summary>
/// Thrown by <see cref="IComplianceGate.Validate"/> when a signing request
/// can't be dispatched under its declared bundle. The message is safe to
/// surface to API callers — no PII, just the violated rule.
/// </summary>
public sealed class ComplianceViolationException : Exception
{
    public ComplianceViolationException(string message) : base(message) { }
    public ComplianceViolationException(string message, Exception inner) : base(message, inner) { }
}
