using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Stampd.Core.Sealing;

/// <summary>
/// Performs the cryptographic part of document sealing. Each provider holds (or remotely
/// references) one or more X.509 signing identities and exposes a primitive "sign these
/// bytes" surface. The CMS / PAdES construction lives above this contract in
/// <c>Stampd.Engine</c>, so all providers — Azure Key Vault, HashiCorp Vault, OpenBao,
/// local certificate, hardware HSM — share a single, narrow API.
/// </summary>
/// <remarks>
/// Design intent: this interface is HSM-friendly. Implementations are not assumed to have
/// a local exportable private key. They are only assumed to be able to (a) hand over the
/// public certificate that will be embedded in the CMS SignedData and (b) produce a signed
/// payload for a given byte string. Anything more (PAdES specifics, timestamping, CMS
/// attribute construction) is the engine's responsibility, not the provider's.
/// </remarks>
public interface ICryptographicSealingProvider
{
    /// <summary>
    /// Short identifying name of the provider as it will be recorded in the audit trail
    /// and on <c>SignedDocumentRecord.SealingProviderName</c>. Examples: "LocalCertificate",
    /// "AzureKeyVault", "Vault", "OpenBao".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns the X.509 certificate (public part plus any intermediate chain) the engine
    /// will embed in the CMS SignedData so verifiers can validate the signature.
    /// </summary>
    Task<X509Certificate2> GetSigningCertificateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs the supplied bytes with the configured key and returns the raw signature.
    /// </summary>
    /// <param name="data">The bytes to sign. The engine passes the DER-encoded signed-attributes blob produced by the CMS construction; implementations should treat it as opaque.</param>
    /// <param name="hashAlgorithm">Hash algorithm to use. Defaults to SHA-256 in callers; provider must support at least SHA-256.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The signature bytes — for RSA-PKCS#1 v1.5, the raw signature; for ECDSA, the DER-encoded (r, s) sequence.</returns>
    Task<byte[]> SignAsync(
        byte[] data,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default);
}
