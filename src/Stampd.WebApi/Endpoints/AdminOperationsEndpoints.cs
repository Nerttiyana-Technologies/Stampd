using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Infrastructure;
using Stampd.WebApi.Services;

namespace Stampd.WebApi.Endpoints;

/// <summary>
/// v2.0 Slice D — admin mutation endpoints. Bulk void, bulk resend, demo cleanup.
/// All three are registered under the <c>adminGroup</c> in Program.cs so they
/// inherit the <c>Admin</c>-only authorization policy.
/// </summary>
internal static class AdminOperationsEndpoints
{
    /// <summary>
    /// Hard cap on bulk-op size. 100 is enough for any reasonable admin batch
    /// (clearing a day's worth of stuck requests) without exposing a runaway
    /// operation that locks up the worker.
    /// </summary>
    private const int MaxBulkSize = 100;

    public static IEndpointRouteBuilder MapAdminOperations(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin").WithTags("AdminOperations");

        group.MapPost("/signing-requests/void", VoidBulkAsync)
            .WithName("AdminBulkVoid")
            .WithSummary("Bulk-void signing requests with an optional shared reason. v2.0 Slice D #203.");

        group.MapPost("/signing-requests/resend-invitation", ResendBulkAsync)
            .WithName("AdminBulkResendInvitation")
            .WithSummary("Bulk-resend invitation emails to the supplied recipient ids. v2.0 Slice D #203.");

        group.MapPost("/cleanup-demo", CleanupDemoAsync)
            .WithName("AdminCleanupDemo")
            .WithSummary("Wipes signing-request lifecycle tables (preserves templates). Development-only. v2.0 Slice D #203.");

        return builder;
    }

    public sealed record BulkVoidRequest(IReadOnlyList<Guid> Ids, string? Reason);
    public sealed record BulkResendRequest(IReadOnlyList<Guid> RecipientIds);
    public sealed record BulkOperationResult(int Succeeded, int Failed, IReadOnlyList<BulkOperationItem> Items);
    public sealed record BulkOperationItem(Guid Id, bool Success, string? Error);

    private static async Task<IResult> VoidBulkAsync(
        [FromBody] BulkVoidRequest body,
        [FromServices] SigningWorkflowService workflow,
        CancellationToken ct)
    {
        if (body.Ids.Count == 0)
        {
            return Results.Problem("At least one id is required.", statusCode: 400);
        }
        if (body.Ids.Count > MaxBulkSize)
        {
            return Results.Problem(
                $"Bulk op cap is {MaxBulkSize} ids per call; got {body.Ids.Count}.",
                statusCode: 400);
        }

        var items = new List<BulkOperationItem>(body.Ids.Count);
        var succeeded = 0;
        var failed = 0;

        // Sequential to keep audit ordering deterministic and to avoid DbContext
        // contention. Slice D scope = correctness + auditability; parallelism is a
        // v2.1 perf polish if real adopters report slowness on 100-row batches.
        foreach (var id in body.Ids)
        {
            try
            {
                var applied = await workflow.VoidAsync(id, body.Reason, ct).ConfigureAwait(false);
                items.Add(new BulkOperationItem(id, applied, applied ? null : "Already voided or not found."));
                if (applied) succeeded++;
                else failed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateConcurrencyException)
            {
                items.Add(new BulkOperationItem(id, false, ex.Message));
                failed++;
            }
        }

        return Results.Ok(new BulkOperationResult(succeeded, failed, items));
    }

    private static async Task<IResult> ResendBulkAsync(
        [FromBody] BulkResendRequest body,
        [FromServices] SigningWorkflowService workflow,
        CancellationToken ct)
    {
        if (body.RecipientIds.Count == 0)
        {
            return Results.Problem("At least one recipientId is required.", statusCode: 400);
        }
        if (body.RecipientIds.Count > MaxBulkSize)
        {
            return Results.Problem(
                $"Bulk op cap is {MaxBulkSize} recipientIds per call; got {body.RecipientIds.Count}.",
                statusCode: 400);
        }

        var items = new List<BulkOperationItem>(body.RecipientIds.Count);
        var succeeded = 0;
        var failed = 0;

        foreach (var id in body.RecipientIds)
        {
            try
            {
                var sent = await workflow.ResendInvitationAsync(id, ct).ConfigureAwait(false);
                items.Add(new BulkOperationItem(id, sent, sent ? null : "Resend skipped — recipient missing or email transport unavailable."));
                if (sent) succeeded++;
                else failed++;
            }
            catch (InvalidOperationException ex)
            {
                items.Add(new BulkOperationItem(id, false, ex.Message));
                failed++;
            }
        }

        return Results.Ok(new BulkOperationResult(succeeded, failed, items));
    }

    public sealed record CleanupDemoResult(
        int SigningRequestsDeleted,
        int RecipientsDeleted,
        int SignedDocumentsDeleted,
        int AuditEventsDeleted,
        int OtpChallengesDeleted,
        int WebhookDeliveriesDeleted);

    private static async Task<IResult> CleanupDemoAsync(
        [FromServices] StampdDbContext db,
        [FromServices] IHostEnvironment env,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        // Two-gate guard: must be Development OR have an explicit override flag.
        // The override exists so adopters running a dedicated staging tenant can
        // opt-in to the cleanup endpoint via config without code changes.
        var explicitlyAllowed = config.GetValue("Stampd:Admin:AllowDemoCleanup", false);
        if (!env.IsDevelopment() && !explicitlyAllowed)
        {
            return Results.Problem(
                "Cleanup endpoint is Development-only by default. Set Stampd:Admin:AllowDemoCleanup=true to enable in other environments.",
                statusCode: 403);
        }

        // Order matters — child rows first, parent last, to satisfy FK constraints
        // even when cascade isn't configured on every relationship. ExecuteDeleteAsync
        // bypasses the change-tracker so this stays cheap even when the tables are
        // large; the row counts come back per-DELETE so the response can summarize.
        var auditRows = await db.AuditEvents.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var recipientRows = await db.Recipients.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var signedDocRows = await db.SignedDocumentRecords.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var sigRequestRows = await db.SigningRequests.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var otpRows = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var webhookRows = await db.WebhookDeliveries.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return Results.Ok(new CleanupDemoResult(
            SigningRequestsDeleted: sigRequestRows,
            RecipientsDeleted: recipientRows,
            SignedDocumentsDeleted: signedDocRows,
            AuditEventsDeleted: auditRows,
            OtpChallengesDeleted: otpRows,
            WebhookDeliveriesDeleted: webhookRows));
    }
}
