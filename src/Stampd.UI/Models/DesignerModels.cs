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
public sealed record CreateSigningRequestBody(
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientAssignmentDto> Recipients);

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
