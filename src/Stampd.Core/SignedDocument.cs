namespace Stampd.Core;

/// <summary>
/// Output of <see cref="IStampdEngine.SignAsync"/>.
/// </summary>
/// <param name="SignedPdf">
/// The fully sealed PDF byte array. Suitable for direct storage, email attachment, or
/// streaming to the recipient for download.
/// </param>
/// <param name="DocumentHashSha256">
/// SHA-256 hash of the signed PDF, hex-encoded lowercase. Captured into the audit trail
/// by the workflow layer.
/// </param>
/// <param name="SignedAtUtc">
/// Wall-clock time the signature was applied, in UTC. If a timestamp authority is used
/// this should be cross-referenced against the embedded TST.
/// </param>
public sealed record SignedDocument(
    ReadOnlyMemory<byte> SignedPdf,
    string DocumentHashSha256,
    DateTimeOffset SignedAtUtc);
