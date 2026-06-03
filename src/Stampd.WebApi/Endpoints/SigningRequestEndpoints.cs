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
            ToResponse(created, http));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        var request = await db.SigningRequests
            .Include(r => r.Recipients).ThenInclude(p => p.Role)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        return request is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(request, http));
    }

    private static SigningRequestResponse ToResponse(SigningRequest request, HttpContext http)
    {
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
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
                    : $"{baseUrl}/api/sign/{r.AccessToken}"))
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
