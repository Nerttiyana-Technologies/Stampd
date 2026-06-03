namespace Stampd.WebApi.Models;

/// <summary>Request body for POST /api/templates/{id}/bulk-send.</summary>
public sealed record BulkSendRequestBody(
    string Subject,
    string? Message,
    IReadOnlyList<BulkSendRow> Rows);

/// <summary>One recipient cohort in a bulk send. Becomes a single SigningRequest.</summary>
public sealed record BulkSendRow(
    IReadOnlyList<BulkSendRecipientAssignment> Recipients,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record BulkSendRecipientAssignment(
    string RoleName,
    string Email,
    string Name);

public sealed record BulkSendJobResponse(
    Guid Id,
    string Status,
    int TotalRows,
    int CompletedRows,
    int FailedRows,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);
