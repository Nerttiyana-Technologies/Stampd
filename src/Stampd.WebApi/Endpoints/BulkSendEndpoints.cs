using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Endpoints;

internal static class BulkSendEndpoints
{
    public static IEndpointRouteBuilder MapBulkSend(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/templates/{id:guid}/bulk-send", DispatchBulkSendAsync)
            .WithName("BulkSendDispatch");

        app.MapGet("/api/bulk-send/{id:guid}", GetBulkSendStatusAsync)
            .WithName("BulkSendStatus");

        return app;
    }

    private static async Task<IResult> DispatchBulkSendAsync(
        Guid id,
        [FromBody] BulkSendRequestBody body,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        if (body.Rows.Count == 0)
        {
            return Results.Problem(
                title: "Rows must contain at least one recipient cohort.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var template = await db.DocumentTemplates
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);
        if (template is null)
        {
            return Results.Problem(
                title: $"Template '{id}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var job = new BulkSendJob
        {
            DocumentTemplateId = id,
            Subject = body.Subject,
            Message = body.Message,
            Status = BulkSendJobStatus.Pending,
            TotalRows = body.Rows.Count,
            CompletedRows = 0,
            FailedRows = 0,
            PendingRowsJson = JsonSerializer.Serialize(body.Rows),
        };
        db.BulkSendJobs.Add(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Accepted(
            uri: $"/api/bulk-send/{job.Id}",
            value: ToResponse(job));
    }

    private static async Task<IResult> GetBulkSendStatusAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var job = await db.BulkSendJobs
            .FirstOrDefaultAsync(j => j.Id == id, ct)
            .ConfigureAwait(false);
        if (job is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ToResponse(job));
    }

    private static BulkSendJobResponse ToResponse(BulkSendJob job)
        => new(
            job.Id,
            job.Status.ToString(),
            job.TotalRows,
            job.CompletedRows,
            job.FailedRows,
            job.CreatedAtUtc,
            job.CompletedAtUtc);
}
