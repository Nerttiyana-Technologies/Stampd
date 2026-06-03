namespace Stampd.Core;

/// <summary>
/// Input to <see cref="IStampdEngine.SignAsync"/> — the source document, the fields to stamp,
/// the signer-supplied content for each field, and the sealing options used to apply the
/// cryptographic signature.
/// </summary>
public sealed record SignatureRequest
{
    /// <summary>
    /// The unsigned source PDF as a byte array.
    /// </summary>
    public required ReadOnlyMemory<byte> SourcePdf { get; init; }

    /// <summary>
    /// All fields placed on the document during template design.
    /// </summary>
    public required IReadOnlyList<SignatureField> Fields { get; init; }

    /// <summary>
    /// The signer-supplied content for each field, keyed by a deterministic field index
    /// (the index into <see cref="Fields"/>). Each value is either a PNG byte array (for
    /// <see cref="SignatureFieldKind.Signature"/> / <see cref="SignatureFieldKind.Initials"/>)
    /// or a UTF-8 encoded string (for date, checkbox, or text).
    /// </summary>
    public required IReadOnlyDictionary<int, ReadOnlyMemory<byte>> FieldValues { get; init; }

    /// <summary>
    /// Cryptographic sealing options. Currently only local certificate signing is supported
    /// in the engine spike.
    /// </summary>
    public required SealingOptions Sealing { get; init; }

    /// <summary>
    /// Free-form metadata recorded into the signature dictionary (Reason, Location, Contact).
    /// Optional.
    /// </summary>
    public SignatureMetadata? Metadata { get; init; }
}

/// <summary>
/// Optional metadata recorded inside the PDF signature dictionary.
/// </summary>
public sealed record SignatureMetadata(
    string? Reason = null,
    string? Location = null,
    string? ContactInfo = null,
    string? SignerName = null);
