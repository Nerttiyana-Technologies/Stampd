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
