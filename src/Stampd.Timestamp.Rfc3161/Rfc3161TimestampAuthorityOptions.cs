namespace Stampd.Timestamp.Rfc3161;

/// <summary>
/// Configuration for a generic <see cref="Rfc3161TimestampAuthorityProvider"/>. Designed to
/// cover the matrix of public, commercial, and internal RFC 3161 TSAs:
/// <list type="bullet">
///   <item><description>FreeTSA — anonymous, no auth.</description></item>
///   <item><description>DigiCert / GlobalSign / Sectigo — HTTP basic auth with an API token.</description></item>
///   <item><description>Internal AD CS / OpenSSL / EJBCA — mutual TLS via client certificate.</description></item>
/// </list>
/// </summary>
public sealed class Rfc3161TimestampAuthorityOptions
{
    /// <summary>
    /// Short identifying name surfaced via <see cref="Stampd.Core.Sealing.ITimestampAuthorityProvider.Name"/>
    /// and recorded onto <c>SignedDocumentRecord.TimestampAuthorityUrl</c> + audit events.
    /// Required.
    /// </summary>
    public string Name { get; set; } = "RFC3161";

    /// <summary>
    /// HTTP endpoint of the TSA. Required.
    /// Examples: <c>https://freetsa.org/tsr</c>, <c>http://timestamp.digicert.com</c>.
    /// </summary>
    public Uri Endpoint { get; set; } = null!;

    /// <summary>
    /// Optional HTTP basic auth username. Some commercial TSAs (paid tier of DigiCert,
    /// GlobalSign GMO, Sectigo's volume tier) authenticate via basic auth.
    /// </summary>
    public string? BasicAuthUsername { get; set; }

    /// <summary>Optional HTTP basic auth password / API token.</summary>
    public string? BasicAuthPassword { get; set; }

    /// <summary>
    /// Optional path to a PKCS#12 file holding a client certificate. Used for mutual TLS
    /// with internal TSAs (Microsoft AD CS Timestamp Service typically uses mTLS).
    /// </summary>
    public string? ClientCertificatePkcs12Path { get; set; }

    /// <summary>Optional password for the client certificate PKCS#12.</summary>
    public string? ClientCertificatePassword { get; set; }

    /// <summary>
    /// Optional TSA policy OID to request. When set, the TSA must honour it or reject the
    /// request. Leave null to let the TSA pick its default policy.
    /// </summary>
    public string? RequestedPolicyOid { get; set; }

    /// <summary>
    /// Whether to ask the TSA to embed its certificate chain in the response. Defaults to
    /// <see langword="true"/> — required for PAdES B-T because Adobe needs the chain to
    /// validate the timestamp without a separate trust-anchor configuration.
    /// </summary>
    public bool RequestTsaCertificate { get; set; } = true;

    /// <summary>
    /// Whether to include a nonce in the request. Defaults to <see langword="true"/>;
    /// disable only for TSAs that reject nonces.
    /// </summary>
    public bool IncludeNonce { get; set; } = true;

    /// <summary>
    /// HTTP request timeout. Defaults to 30 seconds — public TSAs are sometimes slow.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException(
                $"{nameof(Rfc3161TimestampAuthorityOptions)}.{nameof(Name)} is required.");
        }

        if (Endpoint is null)
        {
            throw new InvalidOperationException(
                $"{nameof(Rfc3161TimestampAuthorityOptions)}.{nameof(Endpoint)} is required.");
        }

        if (!string.IsNullOrEmpty(BasicAuthUsername) && BasicAuthPassword is null)
        {
            throw new InvalidOperationException(
                $"{nameof(BasicAuthPassword)} must be provided when {nameof(BasicAuthUsername)} is set.");
        }

        if (!string.IsNullOrEmpty(ClientCertificatePkcs12Path)
            && !File.Exists(ClientCertificatePkcs12Path))
        {
            throw new InvalidOperationException(
                $"Client certificate PKCS#12 file not found at '{ClientCertificatePkcs12Path}'.");
        }
    }
}
