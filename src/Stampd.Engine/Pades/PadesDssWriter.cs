using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Stampd.Engine.Pades;

/// <summary>
/// Writes a PAdES B-LT Document Security Store (DSS) dictionary into a PdfSharp
/// <see cref="PdfDocument"/>'s catalog. Each certificate, OCSP response, and CRL is added
/// as an indirect stream object; the DSS dictionary references them by indirect reference.
/// </summary>
/// <remarks>
/// <para>
/// Structural layout produced (PAdES per ETSI TS 119 142-1 §5.4):
/// </para>
/// <code>
/// Catalog
///   /DSS &lt;&lt;
///     /Certs [ N0R N1R N2R ... ]   ← every cert in the signer chain
///     /OCSPs [ N3R N4R ... ]      ← optional, per-cert OCSP responses
///     /CRLs  [ N5R N6R ... ]      ← optional, per-cert CRLs
///   &gt;&gt;
/// </code>
///
/// <para>
/// <b>Note on placement (v1.1):</b> this writer adds the DSS to the document *before* the
/// digital signature handler runs, so the DSS bytes are covered by the signature's
/// <c>/ByteRange</c>. The strict ETSI profile expects the DSS in an *incremental update
/// after* the signature; PdfSharp 6.x does not currently expose first-class incremental
/// save. Adobe Acrobat accepts both placements and renders Long-Term Validation enabled
/// when the data is present. A v1.2 task tracks moving to true incremental update once
/// PdfSharp gains the API or we drop down to raw byte-level append.
/// </para>
/// </remarks>
internal static class PadesDssWriter
{
    /// <summary>
    /// Adds the <c>/DSS</c> dictionary to the catalog of <paramref name="document"/> and
    /// registers all the cert / OCSP / CRL streams as indirect objects. No-op when
    /// <paramref name="data"/> is empty.
    /// </summary>
    public static void Write(PdfDocument document, PadesRevocationData data)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(data);

        if (data.IsEmpty)
        {
            return;
        }

        var certArray = data.Certificates.Count == 0
            ? null
            : BuildIndirectStreamArray(document, data.Certificates);
        var ocspArray = data.OcspResponses.Count == 0
            ? null
            : BuildIndirectStreamArray(document, data.OcspResponses);
        var crlArray = data.Crls.Count == 0
            ? null
            : BuildIndirectStreamArray(document, data.Crls);

        var dss = new PdfDictionary(document);
        if (certArray is not null)
        {
            dss.Elements["/Certs"] = certArray;
        }

        if (ocspArray is not null)
        {
            dss.Elements["/OCSPs"] = ocspArray;
        }

        if (crlArray is not null)
        {
            dss.Elements["/CRLs"] = crlArray;
        }

        document.Internals.AddObject(dss);

        // Attach to the document catalog. The /DSS key on the catalog is the PAdES B-LT
        // anchor — Adobe specifically looks for /Catalog/DSS to enable Long-Term Validation.
        document.Internals.Catalog.Elements["/DSS"] = PdfInternals.GetReference(dss);
    }

    private static PdfArray BuildIndirectStreamArray(PdfDocument document, IReadOnlyList<byte[]> blobs)
    {
        var array = new PdfArray(document);
        foreach (var blob in blobs)
        {
            var streamObject = new PdfDictionary(document);
            streamObject.CreateStream(blob);
            document.Internals.AddObject(streamObject);
            array.Elements.Add(PdfInternals.GetReference(streamObject));
        }

        return array;
    }

    /// <summary>
    /// PdfSharp 6.x exposes object references through <c>PdfDocument.Internals</c> but the
    /// exact API surface for "get the indirect reference of an object I just added" varies
    /// across patch versions. Wrapping it in one helper makes the eventual fix-up trivial
    /// if the API name changes.
    /// </summary>
    private static class PdfInternals
    {
        public static PdfReference GetReference(PdfObject obj)
        {
            // PdfSharp populates obj.Reference when the object is registered via AddObject.
            var reference = obj.Reference;
            if (reference is null)
            {
                throw new InvalidOperationException(
                    "PdfSharp did not assign an indirect reference to the object after AddObject; "
                    + "DSS embedding cannot proceed.");
            }

            return reference;
        }
    }
}
