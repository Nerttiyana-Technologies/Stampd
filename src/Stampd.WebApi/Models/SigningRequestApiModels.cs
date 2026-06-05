using Stampd.Core;
using Stampd.Core.Entities;

namespace Stampd.WebApi.Models;

/// <summary>Request body for <c>POST /api/signing-requests</c>.</summary>
/// <remarks>
/// <para>
/// <paramref name="SenderEmail"/> and <paramref name="SenderName"/> identify the human
/// initiating the signing request (typically the authenticated dashboard user). When
/// supplied, the sender receives a completion notification email once the last recipient
/// signs (v1.3 #134). <paramref name="SenderEmail"/> is also persisted into
/// <see cref="SigningRequest.CreatedBy"/> so the audit trail names the originator; when
/// omitted, CreatedBy falls back to the legacy <c>"api"</c> sentinel and no completion
/// email is sent.
/// </para>
/// </remarks>
public sealed record CreateSigningRequestBody(
    Guid DocumentTemplateId,
    string Subject,
    string? Message,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<RecipientAssignment> Recipients,
    string? SenderEmail = null,
    string? SenderName = null);

/// <summary>Assigns a real person to a template role.</summary>
public sealed record RecipientAssignment(string RoleName, string Email, string Name);

/// <summary>Response after a signing request is created.</summary>
public sealed record SigningRequestResponse(
    Guid Id,
    Guid DocumentTemplateId,
    string Subject,
    SigningRequestStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<RecipientView> Recipients);

/// <summary>What a recipient looks like in API responses (incl. their access link).</summary>
public sealed record RecipientView(
    Guid Id,
    string Email,
    string Name,
    string? RoleName,
    int RoutingOrder,
    RecipientStatus Status,
    DateTimeOffset? InvitedAtUtc,
    DateTimeOffset? SignedAtUtc,
    string? AccessUrl);

/// <summary>What a recipient sees when they open their signing link.</summary>
public sealed record RecipientSigningView(
    Guid SigningRequestId,
    string Subject,
    string? Message,
    string RecipientName,
    string RecipientEmail,
    RecipientStatus Status,
    IReadOnlyList<RecipientFieldView> Fields,
    bool RequiresIdentityVerification,
    DateTimeOffset? IdentityVerifiedAtUtc,
    string? IdentityVerificationMethod);

/// <summary>Body for <c>POST /api/sign/{accessToken}/initiate-verification</c>.</summary>
public sealed record InitiateVerificationResponse(
    string VerificationId,
    DateTimeOffset ExpiresAtUtc,
    string? UserVisibleHint);

/// <summary>Body for <c>POST /api/sign/{accessToken}/verify-identity</c>.</summary>
public sealed record VerifyIdentityRequest(
    string VerificationId,
    string Code);

/// <summary>Response for <c>POST /api/sign/{accessToken}/verify-identity</c>.</summary>
public sealed record VerifyIdentityResponse(
    bool Succeeded,
    string? FailureReason,
    DateTimeOffset? IdentityVerifiedAtUtc);

/// <summary>A single field the recipient must fill on their signing page.</summary>
public sealed record RecipientFieldView(
    int Index,
    int PageNumber,
    ApiPercentageRect Bounds,
    SignatureFieldKind Kind,
    string? Label,
    bool IsRequired);

/// <summary>Body for <c>POST /api/sign/{accessToken}</c> — recipient submits their values.</summary>
public sealed record SubmitRecipientSignatureRequest(
    IReadOnlyDictionary<int, ApiFieldValue> FieldValues);

/// <summary>What the recipient sees after they submit.</summary>
public sealed record RecipientSubmitResponse(
    RecipientStatus Status,
    SigningRequestStatus WorkflowStatus,
    Guid? SignedDocumentId,
    string? SignedDocumentHashSha256);
