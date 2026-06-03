using System.Security.Cryptography;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core;
using Stampd.Core.Entities;
using Stampd.Core.Storage;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Endpoints;

internal static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplates(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/templates").WithTags("Templates");

        group.MapPost("/", CreateAsync)
            .WithName("CreateTemplate")
            .WithSummary("Uploads a PDF template with field layout and named recipient roles.");

        group.MapGet("/", ListAsync)
            .WithName("ListTemplates")
            .WithSummary("Returns a paged list of templates in the current tenant, newest first.");

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetTemplate")
            .WithSummary("Retrieves a template's metadata, roles, and field layout.");

        group.MapPost("/{id:guid}/sign-immediate", SignImmediateAsync)
            .WithName("SignTemplateImmediate")
            .WithSummary("Apply signer-supplied values to a stored template and return the signed PDF immediately. Skips the recipient workflow.");

        return builder;
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateTemplateRequest body,
        [FromServices] StampdDbContext db,
        [FromServices] IDocumentStorageProvider storage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Problem("Template name is required.", statusCode: 400);
        }

        if (string.IsNullOrWhiteSpace(body.SourcePdfBase64))
        {
            return Results.Problem("sourcePdfBase64 is required.", statusCode: 400);
        }

        byte[] sourcePdf;
        try
        {
            sourcePdf = Convert.FromBase64String(body.SourcePdfBase64);
        }
        catch (FormatException ex)
        {
            return Results.Problem($"sourcePdfBase64 is not valid base64: {ex.Message}", statusCode: 400);
        }

        var storageKey = await storage.StoreAsync(
            sourcePdf,
            logicalName: $"template-{body.Name}.pdf",
            cancellationToken: ct).ConfigureAwait(false);

        var pdfHash = Convert.ToHexString(SHA256.HashData(sourcePdf)).ToLowerInvariant();

        var template = new DocumentTemplate
        {
            Name = body.Name.Trim(),
            Description = body.Description,
            SourcePdfStorageKey = storageKey,
            SourcePdfSha256 = pdfHash,
            CreatedBy = "api", // v1: single tenant + no auth; populate from principal in v2.
        };

        // Roles
        var rolesByName = new Dictionary<string, TemplateRecipientRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var roleDto in body.Roles)
        {
            var role = new TemplateRecipientRole
            {
                Id = Guid.NewGuid(),
                Name = roleDto.Name.Trim(),
                RoutingOrder = roleDto.RoutingOrder,
                RequiresIdentityVerification = roleDto.RequiresIdentityVerification,
            };
            template.Roles.Add(role);
            rolesByName[role.Name] = role;
        }

        // Fields
        foreach (var fieldDto in body.Fields)
        {
            TemplateRecipientRole? assignedRole = null;
            if (!string.IsNullOrWhiteSpace(fieldDto.AssignedRoleName)
                && !rolesByName.TryGetValue(fieldDto.AssignedRoleName, out assignedRole))
            {
                return Results.Problem(
                    $"Field references unknown role '{fieldDto.AssignedRoleName}'.",
                    statusCode: 400);
            }

            template.Fields.Add(new TemplateField
            {
                Id = Guid.NewGuid(),
                AssignedRole = assignedRole,
                AssignedRoleId = assignedRole?.Id,
                PageNumber = fieldDto.PageNumber,
                BoundsX = fieldDto.Bounds.X,
                BoundsY = fieldDto.Bounds.Y,
                BoundsWidth = fieldDto.Bounds.Width,
                BoundsHeight = fieldDto.Bounds.Height,
                Kind = fieldDto.Kind,
                IsRequired = fieldDto.IsRequired,
                Label = fieldDto.Label,
                DefaultValue = fieldDto.DefaultValue,
            });
        }

        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Created($"/api/templates/{template.Id}", ToResponse(template));
    }

    private static async Task<IResult> ListAsync(
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        // SQLite cannot translate ORDER BY on DateTimeOffset columns (text-sort is
        // ambiguous across offsets). Materialize the projection, then sort client-side.
        // Template counts are tenant-bounded so the result set is tiny.
        var items = await db.DocumentTemplates
            .Where(t => !t.IsArchived)
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                description = t.Description,
                createdAtUtc = t.CreatedAtUtc,
                updatedAtUtc = t.UpdatedAtUtc,
                roleCount = t.Roles.Count,
                fieldCount = t.Fields.Count,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(items.OrderByDescending(t => t.createdAtUtc));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var template = await db.DocumentTemplates
            .Include(t => t.Roles)
            .Include(t => t.Fields)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        return template is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(template));
    }

    private static async Task<IResult> SignImmediateAsync(
        Guid id,
        [FromBody] SignTemplateImmediateRequest body,
        [FromServices] StampdDbContext db,
        [FromServices] IDocumentStorageProvider storage,
        [FromServices] IStampdEngine engine,
        [FromServices] Stampd.WebApi.Services.PadesDefaults padesDefaults,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var template = await db.DocumentTemplates
            .Include(t => t.Fields)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        if (template is null)
        {
            return Results.NotFound();
        }

        if (template.IsArchived)
        {
            return Results.Problem("Template is archived and cannot be signed.", statusCode: 409);
        }

        var sourcePdf = await storage.RetrieveAsync(template.SourcePdfStorageKey, ct).ConfigureAwait(false);

        // Order fields deterministically so int-indexed FieldValues map predictably.
        var orderedFields = template.Fields
            .OrderBy(f => f.PageNumber)
            .ThenBy(f => f.BoundsY)
            .ThenBy(f => f.BoundsX)
            .ToList();

        var signatureFields = orderedFields.Select(f =>
            new SignatureField(
                f.PageNumber,
                new PercentageRect(f.BoundsX, f.BoundsY, f.BoundsWidth, f.BoundsHeight),
                f.Kind,
                f.AssignedRoleId?.ToString() ?? "default"))
            .ToArray();

        var fieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(body.FieldValues.Count);
        foreach (var (index, value) in body.FieldValues)
        {
            if (index < 0 || index >= signatureFields.Length)
            {
                return Results.Problem($"FieldValues[{index}] is out of range; template has {signatureFields.Length} fields.", statusCode: 400);
            }

            if (value.ImageBase64 is { Length: > 0 } img)
            {
                fieldValues[index] = Convert.FromBase64String(img);
            }
            else if (value.Text is { } text)
            {
                fieldValues[index] = System.Text.Encoding.UTF8.GetBytes(text);
            }
        }

        var request = new SignatureRequest
        {
            SourcePdf = sourcePdf,
            Fields = signatureFields,
            FieldValues = fieldValues,
            Sealing = padesDefaults.BuildSealingOptions(),
            Metadata = body.Metadata is null ? null : new SignatureMetadata(
                Reason: body.Metadata.Reason,
                Location: body.Metadata.Location,
                ContactInfo: body.Metadata.ContactInfo,
                SignerName: body.Metadata.SignerName),
        };

        var signed = await engine.SignAsync(request, ct).ConfigureAwait(false);
        var bytes = signed.SignedPdf.ToArray();

        return Results.Ok(new SignResponseBody(
            SignedPdfBase64: Convert.ToBase64String(bytes),
            DocumentHashSha256: signed.DocumentHashSha256,
            SignedAtUtc: signed.SignedAtUtc,
            SignedPdfSizeBytes: bytes.Length));
    }

    private static TemplateResponse ToResponse(DocumentTemplate template) =>
        new(
            template.Id,
            template.Name,
            template.Description,
            template.SourcePdfSha256,
            template.CreatedBy,
            template.CreatedAtUtc,
            template.UpdatedAtUtc,
            template.IsArchived,
            template.Roles.Select(r =>
                new TemplateRoleResponse(r.Id, r.Name, r.RoutingOrder, r.RequiresIdentityVerification)).ToList(),
            template.Fields.Select(f =>
                new TemplateFieldResponse(
                    f.Id,
                    f.PageNumber,
                    new ApiPercentageRect(f.BoundsX, f.BoundsY, f.BoundsWidth, f.BoundsHeight),
                    f.Kind,
                    f.AssignedRoleId,
                    template.Roles.FirstOrDefault(r => r.Id == f.AssignedRoleId)?.Name,
                    f.IsRequired,
                    f.Label)).ToList());
}
