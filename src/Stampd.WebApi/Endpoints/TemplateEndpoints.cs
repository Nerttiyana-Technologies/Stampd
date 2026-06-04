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

        group.MapGet("/{id:guid}/pdf", GetPdfAsync)
            .WithName("GetTemplatePdf")
            .WithSummary("Returns the template's source PDF bytes so the designer can rehydrate the canvas on edit.");

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateTemplate")
            .WithSummary("Updates a template's metadata, roles, and field layout. The source PDF is immutable.");

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
        // Sort by the long epoch-ms shadow column so SQLite (which can't translate
        // ORDER BY on TEXT-stored DateTimeOffset reliably) does the work server-side.
        // Index: (TenantId, CreatedAtUtcEpochMs) — see DocumentTemplateConfiguration.
        var items = await db.DocumentTemplates
            .Where(t => !t.IsArchived)
            .OrderByDescending(t => t.CreatedAtUtcEpochMs)
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

        return Results.Ok(items);
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

    private static async Task<IResult> GetPdfAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        [FromServices] IDocumentStorageProvider storage,
        CancellationToken ct)
    {
        var template = await db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        if (template is null)
        {
            return Results.NotFound();
        }

        var pdfBytes = await storage.RetrieveAsync(template.SourcePdfStorageKey, ct).ConfigureAwait(false);
        return Results.File(pdfBytes, "application/pdf", $"template-{template.Name}.pdf");
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateTemplateRequest body,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Problem("Template name is required.", statusCode: 400);
        }

        // Validate first: existence + archived status, without loading children. Reading
        // through an anonymous projection keeps the change tracker clean for the
        // ExecuteUpdate / ExecuteDelete pass below.
        var existing = await db.DocumentTemplates
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.IsArchived })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return Results.NotFound();
        }

        if (existing.IsArchived)
        {
            return Results.Problem("Archived templates cannot be edited.", statusCode: 409);
        }

        // Pre-validate the field payload before we touch the database.
        var incomingRoleNames = new HashSet<string>(
            body.Roles.Select(r => r.Name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var fieldDto in body.Fields)
        {
            if (!string.IsNullOrWhiteSpace(fieldDto.AssignedRoleName)
                && !incomingRoleNames.Contains(fieldDto.AssignedRoleName.Trim()))
            {
                return Results.Problem(
                    $"Field references unknown role '{fieldDto.AssignedRoleName}'.",
                    statusCode: 400);
            }
        }

        // Wipe child collections via raw DELETE — bypasses the change tracker entirely,
        // which sidesteps the SetNull-cascade-on-deleted-field interaction that produced
        // phantom optimistic-concurrency throws when this method used the change tracker.
        //
        // IgnoreQueryFilters is safe here: we've already tenant-validated via the parent
        // existence check above, and every child's FK chains back to the same template.
        // Skipping the filter avoids EF having to translate the navigation-based tenant
        // predicate into a subquery on the DELETE.
        await db.TemplateFields
            .IgnoreQueryFilters()
            .Where(f => f.DocumentTemplateId == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        await db.TemplateRecipientRoles
            .IgnoreQueryFilters()
            .Where(r => r.DocumentTemplateId == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Update scalar fields and rotate the concurrency token in a single statement.
        // ExecuteUpdate doesn't go through the IsConcurrencyToken machinery, so we're
        // free of the WHERE-clause-mismatch failure mode that change-tracker SaveChanges
        // was hitting.
        var now = DateTimeOffset.UtcNow;
        var rowsUpdated = await db.DocumentTemplates
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Name, body.Name.Trim())
                .SetProperty(t => t.Description, body.Description)
                .SetProperty(t => t.UpdatedAtUtc, now)
                .SetProperty(t => t.ConcurrencyToken, Guid.NewGuid()), ct)
            .ConfigureAwait(false);

        if (rowsUpdated == 0)
        {
            // Template disappeared between the existence check and the update. Rare,
            // but possible under concurrent delete.
            return Results.NotFound();
        }

        // Insert the new roles + fields via their own DbSets — by FK, not via the
        // template's navigation. This guarantees the template entity itself is never
        // attached to this DbContext on the write path, so its ConcurrencyToken can't
        // be touched by SaveChanges. (Attaching the template and adding via navigation
        // was causing a phantom optimistic-concurrency throw on the final batch.)
        var rolesByName = new Dictionary<string, TemplateRecipientRole>(StringComparer.OrdinalIgnoreCase);
        var newRoles = new List<TemplateRecipientRole>(body.Roles.Count);
        foreach (var roleDto in body.Roles)
        {
            var role = new TemplateRecipientRole
            {
                Id = Guid.NewGuid(),
                DocumentTemplateId = id,
                Name = roleDto.Name.Trim(),
                RoutingOrder = roleDto.RoutingOrder,
                RequiresIdentityVerification = roleDto.RequiresIdentityVerification,
            };
            newRoles.Add(role);
            rolesByName[role.Name] = role;
        }

        var newFields = new List<TemplateField>(body.Fields.Count);
        foreach (var fieldDto in body.Fields)
        {
            Guid? assignedRoleId = null;
            if (!string.IsNullOrWhiteSpace(fieldDto.AssignedRoleName)
                && rolesByName.TryGetValue(fieldDto.AssignedRoleName, out var assignedRole))
            {
                assignedRoleId = assignedRole.Id;
            }

            newFields.Add(new TemplateField
            {
                Id = Guid.NewGuid(),
                DocumentTemplateId = id,
                AssignedRoleId = assignedRoleId,
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

        if (newRoles.Count > 0)
        {
            db.TemplateRecipientRoles.AddRange(newRoles);
        }
        if (newFields.Count > 0)
        {
            db.TemplateFields.AddRange(newFields);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Re-fetch the freshly updated template (with its newly-attached children) for
        // the response body. AsNoTracking because we have no further mutations to do.
        var refreshed = await db.DocumentTemplates
            .AsNoTracking()
            .Include(t => t.Roles)
            .Include(t => t.Fields)
            .FirstAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);

        return Results.Ok(ToResponse(refreshed));
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
