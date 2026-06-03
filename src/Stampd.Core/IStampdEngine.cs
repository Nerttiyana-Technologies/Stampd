namespace Stampd.Core;

/// <summary>
/// The core contract for stamping a PDF document with signer-supplied content and applying
/// an X.509 cryptographic signature.
/// </summary>
/// <remarks>
/// This interface is intentionally narrow. Routing, identity verification, email delivery,
/// audit recording, and storage all live above the engine — the engine is a pure function
/// from <see cref="SignatureRequest"/> to <see cref="SignedDocument"/>.
/// </remarks>
public interface IStampdEngine
{
    /// <summary>
    /// Stamps every field's value onto the source PDF at its percentage-coordinate position,
    /// flattens the result, and applies a PKCS#7 detached signature using the supplied
    /// sealing options.
    /// </summary>
    /// <param name="request">The signing request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The signed document.</returns>
    Task<SignedDocument> SignAsync(SignatureRequest request, CancellationToken cancellationToken = default);
}
