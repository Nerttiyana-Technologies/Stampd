using System.Security.Cryptography;

namespace Stampd.Core.Sealing;

/// <summary>
/// Requests an RFC 3161 trusted timestamp token (TST) over a piece of data. Embedding the
/// returned token as an unsigned attribute on the CMS SignerInfo promotes a PAdES B-B
/// signature to PAdES B-T — the document gains a time anchor independent of the signer's
/// local clock.
/// </summary>
/// <remarks>
/// Implementations typically POST a TimeStampReq (RFC 3161) to a TSA HTTP endpoint and
/// parse the TimeStampResp. The bytes returned by this interface are the DER-encoded
/// <c>TimeStampToken</c> (the <c>timeStampToken</c> field of the TimeStampResp), not the
/// raw HTTP response.
///
/// Stampd treats the TSA as a pluggable concern so adopters can swap FreeTSA, DigiCert,
/// GlobalSign, Sectigo, an internal Microsoft AD CS TSA, or any compatible provider
/// without changing engine code.
/// </remarks>
public interface ITimestampAuthorityProvider
{
    /// <summary>
    /// Short identifying name of the TSA recorded into the audit trail and onto
    /// <c>SignedDocumentRecord.TimestampAuthorityUrl</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Requests a trusted timestamp over <paramref name="dataToTimestamp"/>.
    /// </summary>
    /// <param name="dataToTimestamp">
    /// The bytes to be timestamped. For a PAdES B-T promotion, this is the raw signature
    /// value extracted from the just-produced CMS SignerInfo.
    /// </param>
    /// <param name="hashAlgorithm">
    /// Hash algorithm the TSA should compute over <paramref name="dataToTimestamp"/>.
    /// Defaults to SHA-256 in callers; providers must support at least SHA-256.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The DER-encoded <c>TimeStampToken</c> (a CMS <c>SignedData</c> structure whose
    /// encapsulated content is a <c>TSTInfo</c>). Suitable for embedding as the value of
    /// the <c>signatureTimeStampToken</c> unsigned attribute on a SignerInfo.
    /// </returns>
    Task<byte[]> RequestTimestampAsync(
        byte[] dataToTimestamp,
        HashAlgorithmName hashAlgorithm,
        CancellationToken cancellationToken = default);
}
