namespace Stampd.Core;

/// <summary>
/// Per-request overrides applied during sealing. The signing identity itself is supplied by
/// the <c>ICryptographicSealingProvider</c> configured on the engine; this record only
/// carries options that may vary per signing request.
/// </summary>
public sealed record SealingOptions
{
    /// <summary>
    /// Optional URL of an RFC 3161 timestamp authority. If set, a trusted timestamp is
    /// embedded into the signature, producing a PAdES B-T signature. If <c>null</c>, the
    /// signature is PAdES B-B (the signer's local clock is the only time reference).
    /// </summary>
    public Uri? TimestampAuthorityUrl { get; init; }

    /// <summary>
    /// Digest algorithm used for the signature. Defaults to SHA-256. The configured
    /// <c>ICryptographicSealingProvider</c> must advertise support for the chosen algorithm.
    /// </summary>
    public string DigestAlgorithm { get; init; } = "SHA-256";
}
