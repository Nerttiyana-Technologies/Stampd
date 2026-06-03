namespace Stampd.Core.Entities;

/// <summary>
/// PAdES (PDF Advanced Electronic Signatures) conformance levels per ETSI EN 319 142-1.
/// </summary>
/// <remarks>
/// Persisted as the integer value alongside <see cref="SignedDocumentRecord"/>. Values must
/// remain stable.
/// </remarks>
public enum PAdESLevel
{
    /// <summary>Basic — CMS signature only, no timestamp.</summary>
    BB = 0,

    /// <summary>Basic with trusted timestamp (RFC 3161).</summary>
    BT = 1,

    /// <summary>Long-term — embeds CRL/OCSP responses for revocation freshness.</summary>
    BLT = 2,

    /// <summary>Long-term with archive timestamp for extended validity.</summary>
    BLTA = 3,
}
