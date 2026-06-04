using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Endpoints;

internal static class SigningRequestEndpoints
{
    public static IEndpointRouteBuilder MapSigningRequests(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/signing-requests").WithTags("SigningRequests");

        group.MapPost("/", CreateAsync)
            .WithName("CreateSigningRequest")
            .WithSummary("Dispatches a new signing request from a template. Returns access URLs for each recipient.");

        group.MapGet("/", ListAsync)
            .WithName("ListSigningRequests")
            .WithSummary("Lists signing requests for the current tenant, newest first. Optionally filter by templateId.");

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetSigningRequest")
            .WithSummary("Returns the current state of a signing request, including recipient statuses.");

        group.MapGet("/{id:guid}/audit", GetAuditAsync)
            .WithName("GetSigningRequestAudit")
            .WithSummary("Returns the append-only audit trail for a signing request (events ordered oldest first).");

        return builder;
    }

    private static async Task<IResult> GetAuditAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var requestExists = await db.SigningRequests
            .AnyAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);
        if (!requestExists)
        {
            return Results.NotFound();
        }

        var events = await db.AuditEvents
            .Where(e => e.SigningRequestId == id)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => new
            {
                e.Id,
                e.EventType,
                e.OccurredAtUtc,
                e.RecipientId,
                e.IpAddress,
                e.UserAgent,
                e.GeoCountry,
                e.GeoCity,
                e.DocumentHashAtEvent,
                e.IsRedacted,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            signingRequestId = id,
            count = events.Count,
            events,
        });
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateSigningRequestBody body,
        [FromServices] SigningWorkflowService workflow,
        [FromServices] WorkflowEmailOptions emailOptions,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Recipients.Count == 0)
        {
            return Results.Problem("At least one recipient is required.", statusCode: 400);
        }

        SigningRequest created;
        try
        {
            created = await workflow.DispatchAsync(body, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 400);
        }

        return Results.Created(
            $"/api/signing-requests/{created.Id}",
            ToResponse(created, http, emailOptions));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        [FromServices] WorkflowEmailOptions emailOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var request = await db.SigningRequests
            .Include(r => r.Recipients).ThenInclude(p => p.Role)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        return request is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(request, http, emailOptions));
    }

    private static async Task<IResult> ListAsync(
        [Microsoft.AspNetCore.Mvc.FromQuery] Guid? templateId,
        [FromServices] StampdDbContext db,
        [FromServices] WorkflowEmailOptions emailOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var query = db.SigningRequests
            .Include(r => r.Recipients).ThenInclude(p => p.Role)
            .Include(r => r.DocumentTemplate)
            .AsQueryable();

        if (templateId is not null)
        {
            query = query.Where(r => r.DocumentTemplateId == templateId.Value);
        }

        // SQLite can't translate ORDER BY on DateTimeOffset (the text-sort is ambiguous
        // across offsets). Materialize the candidate set — capped at 500 so the sort scales
        // — then take the newest 100 client-side. Adding an epoch shadow column the way
        // DocumentTemplate did (see internal/implementation/16) is a v1.3 follow-up if this
        // list grows hot.
        var requests = (await query
                .Take(500)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(100)
            .ToList();

        // Project after materialization so the ToResponse helper (which composes URLs
        // through WorkflowEmailOptions and HttpContext) can do its job without query-tree
        // translation hassles.
        var rows = requests.Select(r => new
        {
            id = r.Id,
            templateId = r.DocumentTemplateId,
            templateName = r.DocumentTemplate?.Name,
            subject = r.Subject,
            status = r.Status,
            createdAtUtc = r.CreatedAtUtc,
            completedAtUtc = r.CompletedAtUtc,
            recipients = ToResponse(r, http, emailOptions).Recipients,
        }).ToList();

        return Results.Ok(rows);
    }

    private static SigningRequestResponse ToResponse(
        SigningRequest request,
        HttpContext http,
        WorkflowEmailOptions emailOptions)
    {
        // When SigningUrlTemplate is configured (typical when a Blazor UI hosts the
        // recipient page on a different origin than the WebApi), use it. Otherwise fall
        // back to the WebApi's own /api/sign/{token} route.
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";

        string BuildAccessUrl(string token) =>
            string.IsNullOrWhiteSpace(emailOptions.SigningUrlTemplate)
                ? $"{baseUrl}/api/sign/{token}"
                : emailOptions.SigningUrlTemplate!.Replace("{accessToken}", token, StringComparison.Ordinal);

        var recipients = request.Recipients
            .OrderBy(r => r.RoutingOrder)
            .Select(r => new RecipientView(
                r.Id,
                r.Email,
                r.Name,
                r.Role?.Name,
                r.RoutingOrder,
                r.Status,
                r.InvitedAtUtc,
                r.SignedAtUtc,
                AccessUrl: r.Status == RecipientStatus.Signed
                    ? null
                    : BuildAccessUrl(r.AccessToken)))
            .ToList();

        return new SigningRequestResponse(
            request.Id,
            request.DocumentTemplateId,
            request.Subject,
            request.Status,
            request.CreatedAtUtc,
            request.SentAtUtc,
            request.CompletedAtUtc,
            recipients);
    }
}
