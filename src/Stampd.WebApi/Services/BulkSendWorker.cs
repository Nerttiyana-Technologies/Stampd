using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Stampd.Core.Entities;
using Stampd.Core.Tenancy;
using Stampd.Infrastructure;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Services;

/// <summary>
/// Background worker that drains pending <see cref="BulkSendJob"/> rows by dispatching each
/// row as a single <see cref="Stampd.Core.Entities.SigningRequest"/>. Polls the DB on a
/// short cycle; capacity is one job at a time per replica, which is plenty for v1 where
/// bulk-send tops out at a few thousand rows per job.
/// </summary>
/// <remarks>
/// Tenant scoping: the lookup runs cross-tenant via <c>IgnoreQueryFilters()</c>; the actual
/// dispatch runs inside a <see cref="TenantScope"/> that pushes the job's tenant onto the
/// async-local, so <see cref="StampdDbContext"/> and downstream services see the right
/// tenant without needing a custom DI override.
/// </remarks>
internal sealed class BulkSendWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BulkSendWorker> _logger;

    public BulkSendWorker(IServiceScopeFactory scopeFactory, ILogger<BulkSendWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BulkSendWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = await TryProcessOneJobAsync(stoppingToken).ConfigureAwait(false);
                if (!processedAny)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BulkSendWorker iteration failed; backing off before retry.");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryProcessOneJobAsync(CancellationToken ct)
    {
        // Step 1: cross-tenant lookup to find a job to process. We do this in its own scope
        // so we don't pollute the dispatch scope's tenant resolution.
        (Guid JobId, Guid TenantId)? candidate = await FindNextJobAsync(ct).ConfigureAwait(false);
        if (candidate is null)
        {
            return false;
        }

        // Step 2: enter the job's tenant scope and process one row.
        using (TenantScope.Enter(candidate.Value.TenantId))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

            var job = await db.BulkSendJobs
                .FirstOrDefaultAsync(j => j.Id == candidate.Value.JobId, ct)
                .ConfigureAwait(false);
            if (job is null)
            {
                return true; // another replica grabbed it
            }

            if (job.Status == BulkSendJobStatus.Pending)
            {
                job.Status = BulkSendJobStatus.InProgress;
                job.StartedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var rows = JsonSerializer.Deserialize<List<BulkSendRow>>(job.PendingRowsJson) ?? [];
            if (rows.Count == 0)
            {
                FinishJob(job);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }

            var workflow = scope.ServiceProvider.GetRequiredService<SigningWorkflowService>();
            var row = rows[0];
            try
            {
                await workflow.DispatchAsync(
                    new CreateSigningRequestBody(
                        DocumentTemplateId: job.DocumentTemplateId,
                        Subject: job.Subject,
                        Message: job.Message,
                        ExpiresAtUtc: row.ExpiresAtUtc,
                        Recipients: row.Recipients
                            .Select(r => new RecipientAssignment(r.RoleName, r.Email, r.Name))
                            .ToArray()),
                    ct).ConfigureAwait(false);

                job.CompletedRows++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                job.FailedRows++;
                RecordFailure(job, rowIndex: job.CompletedRows + job.FailedRows - 1, ex.Message);
                _logger.LogWarning(ex,
                    "Row {Row} of bulk job {JobId} failed; continuing.",
                    job.CompletedRows + job.FailedRows,
                    job.Id);
            }

            rows.RemoveAt(0);
            job.PendingRowsJson = JsonSerializer.Serialize(rows);

            if (rows.Count == 0)
            {
                FinishJob(job);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    private async Task<(Guid JobId, Guid TenantId)?> FindNextJobAsync(CancellationToken ct)
    {
        // Cross-tenant: use TenantScope with Guid.Empty so the DbContext's filter no-ops,
        // matching its existing convention for cross-tenant maintenance access.
        using (TenantScope.Enter(Guid.Empty))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StampdDbContext>();

            var candidate = await db.BulkSendJobs
                .IgnoreQueryFilters()
                .Where(j => j.Status == BulkSendJobStatus.Pending || j.Status == BulkSendJobStatus.InProgress)
                .OrderBy(j => j.CreatedAtUtc)
                .Select(j => new { j.Id, j.TenantId })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return candidate is null ? null : (candidate.Id, candidate.TenantId);
        }
    }

    private static void FinishJob(BulkSendJob job)
    {
        job.Status = job.FailedRows == 0
            ? BulkSendJobStatus.Completed
            : (job.CompletedRows == 0 ? BulkSendJobStatus.Failed : BulkSendJobStatus.PartiallyFailed);
        job.CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void RecordFailure(BulkSendJob job, int rowIndex, string error)
    {
        var failures = JsonSerializer.Deserialize<List<JsonElement>>(job.FailedRowsJson) ?? [];
        if (failures.Count >= 100)
        {
            return;
        }

        var failureList = failures.Select(f => f.GetRawText()).ToList();
        failureList.Add(JsonSerializer.Serialize(new { Row = rowIndex, Error = error }));
        job.FailedRowsJson = "[" + string.Join(",", failureList) + "]";
    }
}
