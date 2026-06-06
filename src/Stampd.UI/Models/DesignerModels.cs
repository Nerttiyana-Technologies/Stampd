using System.Text.Json.Serialization;

namespace Stampd.UI.Models;

/// <summary>Summary row from GET /api/templates.</summary>
public sealed record TemplateSummary(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int RoleCount,
    int FieldCount);

/// <summary>Mirror of WebApi's CreateTemplateRequest body.</summary>
public sealed record CreateTemplateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("sourcePdfBase64")] string SourcePdfBase64,
    [property: JsonPropertyName("roles")] IReadOnlyList<TemplateRoleDto> Roles,
    [property: JsonPropertyName("fields")] IReadOnlyList<TemplateFieldDto> Fields);

public sealed record TemplateRoleDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("routingOrder")] int RoutingOrder,
    [property: JsonPropertyName("requiresIdentityVerification")] bool RequiresIdentityVerification = false);

public sealed record TemplateFieldDto(
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("bounds")] ApiPercentageRect Bounds,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("assignedRoleName")] string? AssignedRoleName,
    [property: JsonPropertyName("isRequired")] bool IsRequired = true,
    [property: JsonPropertyName("label")] string? Label = null);

/// <summary>Mirror of WebApi's UpdateTemplateRequest body (PUT /api/templates/{id}).</summary>
public sealed record UpdateTemplateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("roles")] IReadOnlyList<TemplateRoleDto> Roles,
    [property: JsonPropertyName("fields")] IReadOnlyList<TemplateFieldDto> Fields);

/// <summary>Mirror of WebApi's TemplateResponse — returned by GET /api/templates/{id}.</summary>
public sealed record TemplateDetail(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("sourcePdfSha256")] string SourcePdfSha256,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("isArchived")] bool IsArchived,
    [property: JsonPropertyName("roles")] IReadOnlyList<TemplateRoleDetail> Roles,
    [property: JsonPropertyName("fields")] IReadOnlyList<TemplateFieldDetail> Fields);

public sealed record TemplateRoleDetail(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("routingOrder")] int RoutingOrder,
    [property: JsonPropertyName("requiresIdentityVerification")] bool RequiresIdentityVerification);

public sealed record TemplateFieldDetail(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("bounds")] ApiPercentageRect Bounds,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("assignedRoleId")] Guid? AssignedRoleId,
    [property: JsonPropertyName("assignedRoleName")] string? AssignedRoleName,
    [property: JsonPropertyName("isRequired")] bool IsRequired,
    [property: JsonPropertyName("label")] string? Label);

/// <summary>Mirror of WebApi's CreateSigningRequestBody.</summary>
/// <remarks>
/// <c>SenderEmail</c> + <c>SenderName</c> drive the v1.3 #134 completion notification —
/// the email address that gets the "all recipients signed" message and is persisted into
/// <c>SigningRequest.CreatedBy</c>. Optional for backward compat with pre-1.3 callers.
/// </remarks>
public sealed record CreateSigningRequestBody(
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientAssignmentDto> Recipients,
    [property: JsonPropertyName("senderEmail")] string? SenderEmail = null,
    [property: JsonPropertyName("senderName")] string? SenderName = null);

public sealed record RecipientAssignmentDto(
    [property: JsonPropertyName("roleName")] string RoleName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Mirror of WebApi's SigningRequestResponse — return from POST /api/signing-requests.</summary>
public sealed record SigningRequestResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset? SentAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientViewDto> Recipients);

public sealed record RecipientViewDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("roleName")] string? RoleName,
    [property: JsonPropertyName("routingOrder")] int RoutingOrder,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("invitedAtUtc")] DateTimeOffset? InvitedAtUtc,
    [property: JsonPropertyName("signedAtUtc")] DateTimeOffset? SignedAtUtc,
    [property: JsonPropertyName("accessUrl")] string? AccessUrl);

/// <summary>Mirror of one row returned by GET /api/signing-requests.</summary>
public sealed record SigningRequestSummary(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("templateId")] Guid TemplateId,
    [property: JsonPropertyName("templateName")] string? TemplateName,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientViewDto> Recipients);

/// <summary>
/// Mirror of the paged envelope returned by GET /api/signing-requests (v1.3 #158).
/// <c>TotalPages</c> is always at least 1 even when <c>Total</c> is 0, so UI math
/// reads "Page 1 of 1" rather than a degenerate "Page 1 of 0".
/// </summary>
public sealed record SigningRequestListPage(
    [property: JsonPropertyName("items")] IReadOnlyList<SigningRequestSummary> Items,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalPages")] int TotalPages);

/// <summary>
/// Mirror of GET /api/signing-requests/{id} (v1.3 #135 — sender detail page). Reuses
/// the same wire shape as <c>SigningRequestResponse</c> but typed as a record so the
/// detail page can bind it directly without juggling tuples.
/// </summary>
public sealed record SigningRequestDetail(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset? SentAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientViewDto> Recipients);

/// <summary>
/// Mirror of GET /api/signing-requests/{id}/audit (v1.3 #135). The shape comes back
/// as an envelope with <c>events</c>; the detail page reads them oldest-first and
/// renders them as a timeline alongside the recipients table.
/// </summary>
public sealed record SigningRequestAuditPage(
    [property: JsonPropertyName("signingRequestId")] Guid SigningRequestId,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("events")] IReadOnlyList<AuditEventDto> Events);

/// <summary>
/// Mirror of one audit event row. <c>EventType</c> comes back as a string because the
/// WebApi registers <c>JsonStringEnumConverter</c> globally; the page maps it to a
/// friendly label and resolves the recipient name via the parent's Recipients list.
/// </summary>
public sealed record AuditEventDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("occurredAtUtc")] DateTimeOffset OccurredAtUtc,
    [property: JsonPropertyName("recipientId")] Guid? RecipientId,
    [property: JsonPropertyName("ipAddress")] string? IpAddress,
    [property: JsonPropertyName("documentHashAtEvent")] string? DocumentHashAtEvent);

/// <summary>Working state for one designer-placed field. Index is local to the editor.</summary>
public sealed class DesignerField
{
    public int Index { get; init; }
    public int PageNumber { get; set; } = 1;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 20;
    public double Height { get; set; } = 4;
    public string Kind { get; set; } = "Text";
    public string? AssignedRoleName { get; set; }
    public string? Label { get; set; }
    public bool IsRequired { get; set; } = true;
}

public sealed class DesignerRole
{
    public string Name { get; set; } = string.Empty;
    public int RoutingOrder { get; set; } = 1;
    public bool RequiresIdentityVerification { get; set; }
}
