namespace Stampd.Engine.Pades;

/// <summary>
/// Aggregated revocation material for an entire certificate chain. Produced by
/// <see cref="PadesRevocationFetcher"/> before signing and consumed by
/// <see cref="PadesDssWriter"/> to populate the PDF Document Security Store.
/// </summary>
/// <remarks>
/// One <see cref="OcspResponses"/> or <see cref="Crls"/> entry per cert in the chain that
/// had material available. Order is not significant — the DSS dictionary's <c>/OCSPs</c>
/// and <c>/CRLs</c> arrays are flat.
/// </remarks>
public sealed class PadesRevocationData
{
    /// <summary>DER-encoded cert bytes for every cert in the embedded chain.</summary>
    public IReadOnlyList<byte[]> Certificates { get; init; } = [];

    /// <summary>DER-encoded OCSP responses for certs whose status was fetched via OCSP.</summary>
    public IReadOnlyList<byte[]> OcspResponses { get; init; } = [];

    /// <summary>DER-encoded CRLs for certs whose status was fetched via CRL.</summary>
    public IReadOnlyList<byte[]> Crls { get; init; } = [];

    /// <summary>True when nothing was fetched — used to skip DSS writing entirely.</summary>
    public bool IsEmpty =>
        Certificates.Count == 0
        && OcspResponses.Count == 0
        && Crls.Count == 0;
}
