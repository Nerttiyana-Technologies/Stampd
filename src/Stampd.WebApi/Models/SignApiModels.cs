using Stampd.Core;

namespace Stampd.WebApi.Models;

/// <summary>
/// Request body for <c>POST /api/sign</c>. PDF and any image-field values are base64-encoded
/// so the whole request fits in a single JSON document (avoiding multipart parsing for the
/// MVP). Text fields are sent inline; the server treats <see cref="ApiFieldValue.Text"/>
/// and <see cref="ApiFieldValue.ImageBase64"/> as mutually exclusive.
/// </summary>
public sealed record SignRequestBody(
    string SourcePdfBase64,
    IReadOnlyList<ApiSignatureField> Fields,
    IReadOnlyDictionary<int, ApiFieldValue> FieldValues,
    ApiSignatureMetadata? Metadata = null);

/// <summary>A signature field placement, modelled to mirror <see cref="SignatureField"/>.</summary>
public sealed record ApiSignatureField(
    int PageNumber,
    ApiPercentageRect Bounds,
    SignatureFieldKind Kind,
    string SignerId);

/// <summary>Percentage rectangle [0, 100] in each dimension.</summary>
public sealed record ApiPercentageRect(double X, double Y, double Width, double Height);

/// <summary>
/// Per-field value. Exactly one of <see cref="Text"/> or <see cref="ImageBase64"/> must
/// be set; the server picks based on the field's kind.
/// </summary>
public sealed record ApiFieldValue(string? Text = null, string? ImageBase64 = null);

/// <summary>Optional metadata embedded into the signature dictionary.</summary>
public sealed record ApiSignatureMetadata(
    string? Reason = null,
    string? Location = null,
    string? ContactInfo = null,
    string? SignerName = null);

/// <summary>Response body for <c>POST /api/sign</c>.</summary>
public sealed record SignResponseBody(
    string SignedPdfBase64,
    string DocumentHashSha256,
    DateTimeOffset SignedAtUtc,
    int SignedPdfSizeBytes);
