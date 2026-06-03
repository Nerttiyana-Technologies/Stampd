using Stampd.Core;

namespace Stampd.WebApi.Models;

/// <summary>Request body for <c>POST /api/templates</c>.</summary>
public sealed record CreateTemplateRequest(
    string Name,
    string? Description,
    string SourcePdfBase64,
    IReadOnlyList<TemplateRoleDto> Roles,
    IReadOnlyList<TemplateFieldDto> Fields);

/// <summary>A named slot in a template (e.g. "Customer", "Approver") with routing order.</summary>
public sealed record TemplateRoleDto(
    string Name,
    int RoutingOrder,
    bool RequiresIdentityVerification = false);

/// <summary>A field placement on a template.</summary>
public sealed record TemplateFieldDto(
    int PageNumber,
    ApiPercentageRect Bounds,
    SignatureFieldKind Kind,
    string? AssignedRoleName,
    bool IsRequired = true,
    string? Label = null,
    string? DefaultValue = null);

/// <summary>Response when a template is created or fetched.</summary>
public sealed record TemplateResponse(
    Guid Id,
    string Name,
    string? Description,
    string SourcePdfSha256,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsArchived,
    IReadOnlyList<TemplateRoleResponse> Roles,
    IReadOnlyList<TemplateFieldResponse> Fields);

public sealed record TemplateRoleResponse(
    Guid Id,
    string Name,
    int RoutingOrder,
    bool RequiresIdentityVerification);

public sealed record TemplateFieldResponse(
    Guid Id,
    int PageNumber,
    ApiPercentageRect Bounds,
    SignatureFieldKind Kind,
    Guid? AssignedRoleId,
    string? AssignedRoleName,
    bool IsRequired,
    string? Label);

/// <summary>Request body for <c>POST /api/templates/{id}/sign-immediate</c> — apply values to a stored template and sign now.</summary>
public sealed record SignTemplateImmediateRequest(
    IReadOnlyDictionary<int, ApiFieldValue> FieldValues,
    ApiSignatureMetadata? Metadata = null);
