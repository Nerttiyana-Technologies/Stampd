using System.Text.Json.Serialization;

namespace Stampd.UI.Models;

/// <summary>
/// Mirror of <c>RecipientSigningView</c> from the WebApi. Kept local rather than referenced
/// across the project boundary so the UI can ship independently of API DTO changes.
/// </summary>
public sealed record RecipientSigningView(
    Guid SigningRequestId,
    string Subject,
    string? Message,
    string RecipientName,
    string RecipientEmail,
    string Status,
    IReadOnlyList<RecipientFieldView> Fields,
    bool RequiresIdentityVerification,
    DateTimeOffset? IdentityVerifiedAtUtc,
    string? IdentityVerificationMethod);

public sealed record InitiateVerificationResponse(
    [property: JsonPropertyName("verificationId")] string VerificationId,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("userVisibleHint")] string? UserVisibleHint);

public sealed record VerifyIdentityRequest(
    [property: JsonPropertyName("verificationId")] string VerificationId,
    [property: JsonPropertyName("code")] string Code);

public sealed record VerifyIdentityResponse(
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("identityVerifiedAtUtc")] DateTimeOffset? IdentityVerifiedAtUtc);

public sealed record RecipientFieldView(
    int Index,
    int PageNumber,
    ApiPercentageRect Bounds,
    string Kind,
    string? Label,
    bool IsRequired);

public sealed record ApiPercentageRect(double X, double Y, double Width, double Height);

public sealed record SubmitRecipientSignatureRequest(
    [property: JsonPropertyName("fieldValues")]
    IReadOnlyDictionary<int, ApiFieldValue> FieldValues);

public sealed record ApiFieldValue(
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("imageBase64")] string? ImageBase64 = null);

public sealed record RecipientSubmitResponse(
    string Status,
    string WorkflowStatus,
    Guid? SignedDocumentId,
    string? SignedDocumentHashSha256);
