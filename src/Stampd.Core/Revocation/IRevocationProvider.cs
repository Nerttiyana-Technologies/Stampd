using System.Security.Cryptography.X509Certificates;

namespace Stampd.Core.Revocation;

/// <summary>
/// Fetches revocation information (OCSP responses and/or CRLs) for an X.509 certificate.
/// Used by the engine to promote a PAdES B-T signature to PAdES B-LT by embedding the
/// returned material into the PDF's Document Security Store (DSS) dictionary.
/// </summary>
/// <remarks>
/// PAdES B-LT requires that, at the time of signing, the verifier has all the data needed
/// to determine the signer cert's revocation status. After the signing CA's cert expires,
/// no live OCSP / CRL service may be available. Embedding the response at signing time
/// makes the signature long-term verifiable.
///
/// <para>
/// Implementations are typically:
/// </para>
/// <list type="bullet">
///   <item><description>OCSP HTTP client — parses the cert's Authority Information Access (AIA) extension and POSTs an OCSP request.</description></item>
///   <item><description>CRL HTTP client — parses the cert's CRL Distribution Points (CDP) extension and GETs the CRL.</description></item>
///   <item><description>Composite — tries OCSP first, falls back to CRL.</description></item>
/// </list>
/// </remarks>
public interface IRevocationProvider
{
    /// <summary>
    /// Short identifying name recorded into the audit trail.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Fetches revocation information for <paramref name="certificate"/>, signed by
    /// <paramref name="issuer"/>.
    /// </summary>
    /// <param name="certificate">The certificate whose revocation status is needed.</param>
    /// <param name="issuer">
    /// The certificate that issued <paramref name="certificate"/>. Required for OCSP
    /// request construction; some CRL fetchers ignore it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CertificateRevocationInfo"/> holding the DER-encoded OCSP response
    /// and/or CRL bytes. Either property may be <see langword="null"/> if the
    /// corresponding source is unavailable; the engine ignores null entries.
    /// </returns>
    Task<CertificateRevocationInfo> GetRevocationInfoAsync(
        X509Certificate2 certificate,
        X509Certificate2 issuer,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// DER-encoded revocation material for a single certificate. Either field may be null if
/// the provider could not (or chose not to) fetch the corresponding source.
/// </summary>
/// <param name="OcspResponse">
/// DER-encoded RFC 6960 OCSP response (basic response). Embedded into the DSS dictionary's
/// <c>/OCSPs</c> array when non-null.
/// </param>
/// <param name="Crl">
/// DER-encoded X.509 CRL. Embedded into the DSS dictionary's <c>/CRLs</c> array when
/// non-null.
/// </param>
public sealed record CertificateRevocationInfo(
    byte[]? OcspResponse,
    byte[]? Crl)
{
    /// <summary>Empty info — useful when a provider has nothing to contribute.</summary>
    public static readonly CertificateRevocationInfo Empty = new(null, null);

    /// <summary>True when neither OCSP nor CRL bytes are present.</summary>
    public bool IsEmpty => OcspResponse is null && Crl is null;
}
