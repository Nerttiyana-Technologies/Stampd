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

/// <summary>Mirror of WebApi's CreateSigningRequestBody.</summary>
/// <remarks>
/// <c>SenderEmail</c> + <c>SenderName</c> drive the v1.3 #134 completion notification —
/// the email address that gets the "all recipients signed" message and is persisted into
/// <c>SigningRequest.CreatedBy</c>. Optional for backward compat with pre-1.3 callers.
/// </remarks>
public sealed record CreateSigningRequestBody(
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientAssignmentDto> Recipients,
    [property: JsonPropertyName("senderEmail")] string? SenderEmail = null,
    [property: JsonPropertyName("senderName")] string? SenderName = null);

public sealed record RecipientAssignmentDto(
    [property: JsonPropertyName("roleName")] string RoleName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Mirror of WebApi's SigningRequestResponse — return from POST /api/signing-requests.</summary>
public sealed record SigningRequestResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset? SentAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientViewDto> Recipients);

public sealed record RecipientViewDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("roleName")] string? RoleName,
    [property: JsonPropertyName("routingOrder")] int RoutingOrder,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("invitedAtUtc")] DateTimeOffset? InvitedAtUtc,
    [property: JsonPropertyName("signedAtUtc")] DateTimeOffset? SignedAtUtc,
    [property: JsonPropertyName("accessUrl")] string? AccessUrl);

/// <summary>Mirror of one row returned by GET /api/signing-requests.</summary>
public sealed record SigningRequestSummary(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("templateId")] Guid TemplateId,
    [property: JsonPropertyName("templateName")] string? TemplateName,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientViewDto> Recipients);

/// <summary>
/// v2.0 Slice B — filter + sort knobs the SigningRequests page packs into the API
/// query string. All fields are optional; a fully-null filter means "v1.3 behavior:
/// newest dispatched first, no filter". Status is multi-select (chip group); date
/// fields cover the dispatched-at window only — completed-at filtering can be a
/// future polish.
/// </summary>
public sealed record SigningRequestListFilter(
    IReadOnlyList<string>? Status = null,
    Guid? TemplateId = null,
    string? SenderEmail = null,
    string? RecipientEmail = null,
    DateTimeOffset? DispatchedFrom = null,
    DateTimeOffset? DispatchedTo = null,
    string? SortBy = null,
    string? Direction = null);

/// <summary>
/// Mirror of the paged envelope returned by GET /api/signing-requests (v1.3 #158).
/// <c>TotalPages</c> is always at least 1 even when <c>Total</c> is 0, so UI math
/// reads "Page 1 of 1" rather than a degenerate "Page 1 of 0".
/// </summary>
public sealed record SigningRequestListPage(
    [property: JsonPropertyName("items")] IReadOnlyList<SigningRequestSummary> Items,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalPages")] int TotalPages);

/// <summary>
/// Mirror of GET /api/signing-requests/{id} (v1.3 #135 — sender detail page). Reuses
/// the same wire shape as <c>SigningRequestResponse</c> but typed as a record so the
/// detail page can bind it directly without juggling tuples.
/// </summary>
public sealed record SigningRequestDetail(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("documentTemplateId")] Guid DocumentTemplateId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("sentAtUtc")] DateTimeOffset? SentAtUtc,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc,
    [property: JsonPropertyName("recipients")] IReadOnlyList<RecipientViewDto> Recipients);

/// <summary>
/// Mirror of GET /api/signing-requests/{id}/audit (v1.3 #135). The shape comes back
/// as an envelope with <c>events</c>; the detail page reads them oldest-first and
/// renders them as a timeline alongside the recipients table.
/// </summary>
public sealed record SigningRequestAuditPage(
    [property: JsonPropertyName("signingRequestId")] Guid SigningRequestId,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("events")] IReadOnlyList<AuditEventDto> Events);

/// <summary>
/// Mirror of one audit event row. <c>EventType</c> comes back as a string because the
/// WebApi registers <c>JsonStringEnumConverter</c> globally; the page maps it to a
/// friendly label and resolves the recipient name via the parent's Recipients list.
/// </summary>
public sealed record AuditEventDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("occurredAtUtc")] DateTimeOffset OccurredAtUtc,
    [property: JsonPropertyName("recipientId")] Guid? RecipientId,
    [property: JsonPropertyName("ipAddress")] string? IpAddress,
    [property: JsonPropertyName("documentHashAtEvent")] string? DocumentHashAtEvent);

/// <summary>
/// v2.0 Slice A — mirror of GET /api/admin/dashboard. Hero-row tile counts.
/// </summary>
public sealed record AdminDashboardSummary(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("inProgress")] int InProgress,
    [property: JsonPropertyName("declined")] int Declined,
    [property: JsonPropertyName("completionRatePercent")] double CompletionRatePercent);

/// <summary>v2.0 Slice A — mirror of GET /api/admin/dashboard/trend.</summary>
public sealed record AdminDashboardTrend(
    [property: JsonPropertyName("windowDays")] int WindowDays,
    [property: JsonPropertyName("buckets")] IReadOnlyList<AdminTrendBucket> Buckets);

public sealed record AdminTrendBucket(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("dispatched")] int Dispatched,
    [property: JsonPropertyName("completed")] int Completed);

/// <summary>v2.0 Slice A — mirror of GET /api/admin/dashboard/top-templates.</summary>
public sealed record AdminTopTemplates(
    [property: JsonPropertyName("take")] int Take,
    [property: JsonPropertyName("items")] IReadOnlyList<AdminTopTemplateRow> Items);

public sealed record AdminTopTemplateRow(
    [property: JsonPropertyName("templateId")] Guid TemplateId,
    [property: JsonPropertyName("templateName")] string TemplateName,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("completionRatePercent")] double CompletionRatePercent);

/// <summary>v2.0 Slice D — mirror of POST /api/admin/cleanup-demo response.</summary>
public sealed record CleanupDemoResult(
    [property: JsonPropertyName("signingRequestsDeleted")] int SigningRequestsDeleted,
    [property: JsonPropertyName("recipientsDeleted")] int RecipientsDeleted,
    [property: JsonPropertyName("signedDocumentsDeleted")] int SignedDocumentsDeleted,
    [property: JsonPropertyName("auditEventsDeleted")] int AuditEventsDeleted,
    [property: JsonPropertyName("otpChallengesDeleted")] int OtpChallengesDeleted,
    [property: JsonPropertyName("webhookDeliveriesDeleted")] int WebhookDeliveriesDeleted);

/// <summary>v2.0 Slice D — mirror of bulk-op result (void / resend).</summary>
public sealed record BulkOperationResult(
    [property: JsonPropertyName("succeeded")] int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("items")] IReadOnlyList<BulkOperationItem> Items);

public sealed record BulkOperationItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>v2.0 Slice C — GET /api/admin/analytics/funnel mirror. v2.2 adds prior-window comparison + request-weighted view.</summary>
public sealed record AdminAnalyticsFunnel(
    [property: JsonPropertyName("windowDays")] int WindowDays,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("stages")] AdminFunnelStages Stages,
    [property: JsonPropertyName("dropOff")] AdminFunnelDropOff DropOff,
    [property: JsonPropertyName("conversionRatePercent")] double ConversionRatePercent,
    // v2.2 #223 — nullable so older WebApi instances still deserialize. UI hides
    // the delta chip when the field is absent.
    [property: JsonPropertyName("previousWindow")] AdminFunnelPreviousWindow? PreviousWindow = null,
    [property: JsonPropertyName("deltas")] AdminFunnelDeltas? Deltas = null,
    // v2.2 #225 — request-weighted view. Null for v2.1 and earlier WebApi instances.
    [property: JsonPropertyName("requestWeighted")] AdminFunnelRequestWeighted? RequestWeighted = null,
    // v2.3 #229 — weekday/weekend split. Null for pre-v2.3.
    [property: JsonPropertyName("byDayBucket")] AdminFunnelByDayBucket? ByDayBucket = null);

public sealed record AdminFunnelStages(
    [property: JsonPropertyName("invited")] int Invited,
    [property: JsonPropertyName("viewed")] int Viewed,
    [property: JsonPropertyName("signed")] int Signed,
    [property: JsonPropertyName("declined")] int Declined,
    [property: JsonPropertyName("expired")] int Expired);

public sealed record AdminFunnelDropOff(
    [property: JsonPropertyName("invitedToViewedPercent")] double InvitedToViewedPercent,
    [property: JsonPropertyName("viewedToSignedPercent")] double ViewedToSignedPercent);

/// <summary>v2.2 #223 — prior-window funnel snapshot.</summary>
public sealed record AdminFunnelPreviousWindow(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("stages")] AdminFunnelStages Stages,
    [property: JsonPropertyName("conversionRatePercent")] double ConversionRatePercent);

/// <summary>v2.2 #223 — funnel deltas (current vs prior).</summary>
public sealed record AdminFunnelDeltas(
    [property: JsonPropertyName("totalPercent")] double TotalPercent,
    [property: JsonPropertyName("signedPercent")] double SignedPercent,
    [property: JsonPropertyName("conversionRatePercentPoints")] double ConversionRatePercentPoints);

/// <summary>v2.3 #229 — weekday vs weekend split on the funnel.</summary>
public sealed record AdminFunnelByDayBucket(
    [property: JsonPropertyName("weekday")] AdminFunnelDayBucket Weekday,
    [property: JsonPropertyName("weekend")] AdminFunnelDayBucket Weekend);

public sealed record AdminFunnelDayBucket(
    [property: JsonPropertyName("invited")] int Invited,
    [property: JsonPropertyName("signed")] int Signed,
    [property: JsonPropertyName("conversionRatePercent")] double ConversionRatePercent);

/// <summary>v2.2 #225 — request-weighted funnel (a 2-of-3 signed request = 0.67).</summary>
public sealed record AdminFunnelRequestWeighted(
    [property: JsonPropertyName("requestCount")] int RequestCount,
    [property: JsonPropertyName("fullyCompletedRequests")] int FullyCompletedRequests,
    [property: JsonPropertyName("weightedCompletionFraction")] double WeightedCompletionFraction,
    [property: JsonPropertyName("weightedCompletionPercent")] double WeightedCompletionPercent,
    [property: JsonPropertyName("previousWindow")] AdminFunnelRequestWeightedPreviousWindow? PreviousWindow = null,
    [property: JsonPropertyName("deltas")] AdminFunnelRequestWeightedDeltas? Deltas = null);

public sealed record AdminFunnelRequestWeightedPreviousWindow(
    [property: JsonPropertyName("requestCount")] int RequestCount,
    [property: JsonPropertyName("fullyCompletedRequests")] int FullyCompletedRequests,
    [property: JsonPropertyName("weightedCompletionPercent")] double WeightedCompletionPercent);

public sealed record AdminFunnelRequestWeightedDeltas(
    [property: JsonPropertyName("weightedCompletionPercentPoints")] double WeightedCompletionPercentPoints);

/// <summary>v2.0 Slice C — GET /api/admin/analytics/time-to-sign mirror. v2.2 adds prior-window comparison + per-role segmentation.</summary>
public sealed record AdminTimeToSign(
    [property: JsonPropertyName("windowDays")] int WindowDays,
    [property: JsonPropertyName("take")] int Take,
    [property: JsonPropertyName("items")] IReadOnlyList<AdminTimeToSignRow> Items,
    // v2.2 #226 — per-role segmentation. Null when talking to a pre-v2.2 WebApi.
    [property: JsonPropertyName("byRole")] IReadOnlyList<AdminTimeToSignByRoleRow>? ByRole = null);

public sealed record AdminTimeToSignRow(
    [property: JsonPropertyName("templateId")] Guid TemplateId,
    [property: JsonPropertyName("templateName")] string TemplateName,
    [property: JsonPropertyName("signedCount")] int SignedCount,
    [property: JsonPropertyName("avgMinutes")] double AvgMinutes,
    [property: JsonPropertyName("medianMinutes")] double MedianMinutes,
    // v2.2 #223 — per-template prior-window comparison. Null when the template is
    // new in the current window (no baseline to compare to).
    [property: JsonPropertyName("previousWindow")] AdminTimeToSignPreviousWindow? PreviousWindow = null,
    [property: JsonPropertyName("avgMinutesDeltaPercent")] double? AvgMinutesDeltaPercent = null,
    [property: JsonPropertyName("medianMinutesDeltaPercent")] double? MedianMinutesDeltaPercent = null);

public sealed record AdminTimeToSignPreviousWindow(
    [property: JsonPropertyName("signedCount")] int SignedCount,
    [property: JsonPropertyName("avgMinutes")] double AvgMinutes,
    [property: JsonPropertyName("medianMinutes")] double MedianMinutes);

/// <summary>v2.2 #226 — per-role aggregation row.</summary>
public sealed record AdminTimeToSignByRoleRow(
    [property: JsonPropertyName("roleName")] string RoleName,
    [property: JsonPropertyName("signedCount")] int SignedCount,
    [property: JsonPropertyName("avgMinutes")] double AvgMinutes,
    [property: JsonPropertyName("medianMinutes")] double MedianMinutes,
    [property: JsonPropertyName("previousWindow")] AdminTimeToSignPreviousWindow? PreviousWindow = null,
    [property: JsonPropertyName("avgMinutesDeltaPercent")] double? AvgMinutesDeltaPercent = null,
    [property: JsonPropertyName("medianMinutesDeltaPercent")] double? MedianMinutesDeltaPercent = null);

/// <summary>v2.0 Slice C — GET /api/admin/analytics/identity-verification mirror. v2.2 adds prior-window comparison + per-channel breakdown.</summary>
public sealed record AdminIdentityVerification(
    [property: JsonPropertyName("windowDays")] int WindowDays,
    [property: JsonPropertyName("requiredCount")] int RequiredCount,
    [property: JsonPropertyName("verifiedCount")] int VerifiedCount,
    [property: JsonPropertyName("verifyRatePercent")] double VerifyRatePercent,
    [property: JsonPropertyName("initiatesCount")] int InitiatesCount,
    [property: JsonPropertyName("lockedOutCount")] int LockedOutCount,
    [property: JsonPropertyName("previousWindow")] AdminIdentityVerificationPreviousWindow? PreviousWindow = null,
    [property: JsonPropertyName("deltas")] AdminIdentityVerificationDeltas? Deltas = null,
    // v2.2 #224 — per-channel breakdown for the lockout-by-method chart. Null when
    // talking to a v2.1 or earlier WebApi instance.
    [property: JsonPropertyName("byChannel")] AdminIdentityVerificationByChannel? ByChannel = null);

public sealed record AdminIdentityVerificationPreviousWindow(
    [property: JsonPropertyName("requiredCount")] int RequiredCount,
    [property: JsonPropertyName("verifiedCount")] int VerifiedCount,
    [property: JsonPropertyName("verifyRatePercent")] double VerifyRatePercent,
    [property: JsonPropertyName("initiatesCount")] int InitiatesCount,
    [property: JsonPropertyName("lockedOutCount")] int LockedOutCount);

public sealed record AdminIdentityVerificationDeltas(
    [property: JsonPropertyName("requiredCountPercent")] double RequiredCountPercent,
    [property: JsonPropertyName("verifiedCountPercent")] double VerifiedCountPercent,
    [property: JsonPropertyName("verifyRatePercentPoints")] double VerifyRatePercentPoints,
    [property: JsonPropertyName("initiatesCountPercent")] double InitiatesCountPercent,
    [property: JsonPropertyName("lockedOutCountPercent")] double LockedOutCountPercent);

/// <summary>v2.2 #224 — per-channel IV counts (Email / SMS / KBA).</summary>
public sealed record AdminIdentityVerificationByChannel(
    [property: JsonPropertyName("email")] AdminIdentityVerificationChannelCounts Email,
    [property: JsonPropertyName("sms")] AdminIdentityVerificationChannelCounts Sms,
    [property: JsonPropertyName("kba")] AdminIdentityVerificationChannelCounts Kba);

public sealed record AdminIdentityVerificationChannelCounts(
    [property: JsonPropertyName("initiatesCount")] int InitiatesCount,
    [property: JsonPropertyName("verifiedCount")] int VerifiedCount,
    [property: JsonPropertyName("lockedOutCount")] int LockedOutCount);

/// <summary>v2.3 #230 — webhook-delivery health rollup.</summary>
public sealed record AdminWebhooksHealth(
    [property: JsonPropertyName("totalEndpoints")] int TotalEndpoints,
    [property: JsonPropertyName("activeEndpoints")] int ActiveEndpoints,
    [property: JsonPropertyName("degradedEndpoints")] int DegradedEndpoints,
    [property: JsonPropertyName("pendingDeliveries")] int PendingDeliveries,
    [property: JsonPropertyName("retryingDeliveries")] int RetryingDeliveries,
    [property: JsonPropertyName("failuresLast24h")] int FailuresLast24h,
    [property: JsonPropertyName("recentFailures")] IReadOnlyList<AdminWebhookRecentFailure> RecentFailures);

public sealed record AdminWebhookRecentFailure(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("endpointUrl")] string EndpointUrl,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("attemptCount")] int AttemptCount,
    [property: JsonPropertyName("lastResponseStatus")] int? LastResponseStatus,
    [property: JsonPropertyName("lastErrorMessage")] string? LastErrorMessage,
    [property: JsonPropertyName("nextAttemptAtUtc")] DateTimeOffset NextAttemptAtUtc);

/// <summary>v2.3 #231 — per-sender productivity table.</summary>
public sealed record AdminBySender(
    [property: JsonPropertyName("windowDays")] int WindowDays,
    [property: JsonPropertyName("take")] int Take,
    [property: JsonPropertyName("items")] IReadOnlyList<AdminBySenderRow> Items);

public sealed record AdminBySenderRow(
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("dispatched")] int Dispatched,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("voided")] int Voided,
    [property: JsonPropertyName("completionRatePercent")] double CompletionRatePercent,
    [property: JsonPropertyName("avgTimeToSignMinutes")] double AvgTimeToSignMinutes);

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
