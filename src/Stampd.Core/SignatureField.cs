namespace Stampd.Core;

/// <summary>
/// A single placement of signing collateral on one page of a document.
/// </summary>
/// <param name="PageNumber">One-based page index where this field appears.</param>
/// <param name="Bounds">Position and size in percentage coordinates relative to the page.</param>
/// <param name="Kind">The kind of input this field captures.</param>
/// <param name="SignerId">
/// Stable identifier of the recipient this field belongs to. For the engine spike a single
/// "signer" is sufficient; multi-recipient routing is a v1 workflow concern, not an engine
/// concern.
/// </param>
public sealed record SignatureField(
    int PageNumber,
    PercentageRect Bounds,
    SignatureFieldKind Kind,
    string SignerId);

/// <summary>
/// The kind of input a <see cref="SignatureField"/> captures.
/// </summary>
public enum SignatureFieldKind
{
    /// <summary>A drawn signature (raster image overlaid onto the PDF).</summary>
    Signature,

    /// <summary>A drawn or typed initials block.</summary>
    Initials,

    /// <summary>A date stamp populated from the signing event.</summary>
    Date,

    /// <summary>A checkbox marked at signing time.</summary>
    Checkbox,

    /// <summary>Freeform text supplied by the signer.</summary>
    Text,
}
