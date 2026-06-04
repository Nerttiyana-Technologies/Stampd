namespace Stampd.Core.Entities;

/// <summary>
/// A request to dispatch a single template to many recipient cohorts in one API call. Each
/// cohort becomes one <see cref="SigningRequest"/>; the bulk job tracks aggregate progress.
/// </summary>
/// <remarks>
/// The bulk-send API call returns immediately with the job id; processing happens
/// asynchronously on a hosted background worker so the request thread isn't tied up
/// emitting hundreds of envelopes. Poll <c>GET /api/bulk-send/{id}</c> for progress.
/// </remarks>
public sealed class BulkSendJob
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentTemplateId { get; set; }

    public BulkSendJobStatus Status { get; set; }

    public int TotalRows { get; set; }
    public int CompletedRows { get; set; }
    public int FailedRows { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>
    /// Unix epoch milliseconds copy of <see cref="CreatedAtUtc"/>. Lets BulkSendWorker
    /// pick the oldest pending job via server-side ORDER BY instead of materializing all
    /// candidates. Maintained on insert.
    /// </summary>
    public long CreatedAtUtcEpochMs { get; set; }

    /// <summary>Default subject applied to every dispatched SigningRequest.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Default message body applied to every dispatched SigningRequest.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// JSON payload of remaining rows for the worker to process. Each row is a recipient
    /// cohort — typically <c>{ "RoleAssignments": [...] }</c>. Drained as rows complete.
    /// </summary>
    public string PendingRowsJson { get; set; } = "[]";

    /// <summary>
    /// JSON array of <c>{ "Row": N, "Error": "..." }</c> entries describing failed rows.
    /// Capped at the first 100 failures to keep the row bounded.
    /// </summary>
    public string FailedRowsJson { get; set; } = "[]";
}

public enum BulkSendJobStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    PartiallyFailed = 3,
    Failed = 4,
}
