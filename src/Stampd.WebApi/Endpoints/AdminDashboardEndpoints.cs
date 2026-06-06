using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>
/// v2.0 Slice A — admin dashboard read-only endpoints. All three live under <c>/api/admin/*</c>
/// and inherit the <c>AdminOnly</c> policy from the adminGroup in Program.cs. None of them
/// mutate state; bulk operations land in Slice D under the same group.
/// </summary>
internal static class AdminDashboardEndpoints
{
    /// <summary>
    /// Default trend window. The summary endpoint uses this to compute the
    /// "completion rate over the window" field; the trend endpoint uses it as the
    /// default <c>days</c> query param when the caller doesn't supply one.
    /// </summary>
    private const int DefaultTrendDays = 30;

    /// <summary>
    /// Top-N templates result size when the caller doesn't supply a <c>take</c> value.
    /// 5 fits the dashboard card cleanly; 20 is the hard cap to keep the response
    /// bounded.
    /// </summary>
    private const int DefaultTopTemplatesTake = 5;
    private const int MaxTopTemplatesTake = 20;

    public static IEndpointRouteBuilder MapAdminDashboard(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/dashboard").WithTags("AdminDashboard");

        group.MapGet("/", GetSummaryAsync)
            .WithName("GetAdminDashboardSummary")
            .WithSummary("Counts by signing-request status + tenant-wide completion rate. v2.0 #188.");

        group.MapGet("/trend", GetTrendAsync)
            .WithName("GetAdminDashboardTrend")
            .WithSummary("Daily dispatched + completed counts over a configurable window. v2.0 #189.");

        group.MapGet("/top-templates", GetTopTemplatesAsync)
            .WithName("GetAdminDashboardTopTemplates")
            .WithSummary("Top N templates by request volume + per-template completion rate. v2.0 #190.");

        return builder;
    }

    /// <summary>
    /// Returns the tile counts for the dashboard hero row. One round-trip via a single
    /// projection — no per-status round-trip waste. Tenant scoping is handled by the
    /// existing query filters on <see cref="StampdDbContext"/>.
    /// </summary>
    private static async Task<IResult> GetSummaryAsync(
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        // GroupBy(_ => 1) lets us aggregate every row through a single anonymous group,
        // which EF translates to a single SELECT with conditional COUNTs. Cheap on the
        // (TenantId, CreatedAtUtcEpochMs) composite that the v1.3 epoch sort columns
        // added — the WHERE TenantId = @tenant filter is index-covered.
        var summary = await db.SigningRequests
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Completed = g.Count(r => r.Status == SigningRequestStatus.Completed),
                InProgress = g.Count(r =>
                    r.Status == SigningRequestStatus.Sent ||
                    r.Status == SigningRequestStatus.InProgress),
                Declined = g.Count(r =>
                    r.Status == SigningRequestStatus.Declined ||
                    r.Status == SigningRequestStatus.Voided ||
                    r.Status == SigningRequestStatus.Expired),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Empty tenant — return zeros so the UI renders the card with explicit "0"
        // tiles rather than degrading to a load-error state.
        var total = summary?.Total ?? 0;
        var completed = summary?.Completed ?? 0;
        var inProgress = summary?.InProgress ?? 0;
        var declined = summary?.Declined ?? 0;

        var completionRate = total == 0
            ? 0.0
            : Math.Round(completed * 100.0 / total, 1);

        return Results.Ok(new
        {
            total,
            completed,
            inProgress,
            declined,
            completionRatePercent = completionRate,
        });
    }

    /// <summary>
    /// Daily aggregate for the last <paramref name="days"/> (default 30). Each bucket
    /// carries the date + dispatched count + completed count for THAT day, so the UI
    /// can render either two stacked area charts or one line + delta marker.
    /// </summary>
    private static async Task<IResult> GetTrendAsync(
        [FromQuery] int? days,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultTrendDays, 1, 365);
        var since = DateTimeOffset.UtcNow.Date.AddDays(-(window - 1));
        var sinceEpochMs = new DateTimeOffset(since, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // Use the v1.3 epoch shadow column for the WHERE — guaranteed translatable to
        // SQL on every provider, no DateTimeOffset comparison gotcha (see Doc 16).
        // After filtering, materialize the small slice and bucket client-side: EF Core
        // doesn't have a portable date_trunc translation, and at most 365 days * a few
        // requests per day is a small materialization cost.
        var raw = await db.SigningRequests
            .Where(r => r.CreatedAtUtcEpochMs >= sinceEpochMs)
            .Select(r => new
            {
                r.CreatedAtUtc,
                r.CompletedAtUtc,
                r.Status,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Bucket by UTC date. We compute the dispatched bucket from CreatedAtUtc and
        // the completed bucket from CompletedAtUtc — they're independent: a request
        // dispatched today and completed tomorrow contributes to today's "dispatched"
        // and tomorrow's "completed".
        var dispatchedByDay = raw
            .GroupBy(r => r.CreatedAtUtc.UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => g.Count());
        var completedByDay = raw
            .Where(r => r.CompletedAtUtc is not null && r.CompletedAtUtc.Value.UtcDateTime.Date >= since.Date)
            .GroupBy(r => r.CompletedAtUtc!.Value.UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        // Emit a row per day in the window so the UI's chart has uniform x-axis ticks
        // even when no activity happened on a given day.
        var buckets = new List<object>(window);
        for (var i = 0; i < window; i++)
        {
            var day = since.Date.AddDays(i);
            buckets.Add(new
            {
                date = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                dispatched = dispatchedByDay.GetValueOrDefault(day, 0),
                completed = completedByDay.GetValueOrDefault(day, 0),
            });
        }

        return Results.Ok(new { windowDays = window, buckets });
    }

    /// <summary>
    /// Top N templates by signing-request count, with each row's completion rate.
    /// Defaults to the 5 most-used templates; the <c>take</c> query param widens
    /// up to <see cref="MaxTopTemplatesTake"/>.
    /// </summary>
    private static async Task<IResult> GetTopTemplatesAsync(
        [FromQuery] int? take,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var n = Math.Clamp(take ?? DefaultTopTemplatesTake, 1, MaxTopTemplatesTake);

        // Group by template + project counts in one query. Joining to DocumentTemplate
        // pulls the name; the join is on the indexed FK so it's cheap.
        var top = await db.SigningRequests
            .GroupBy(r => r.DocumentTemplateId)
            .Select(g => new
            {
                TemplateId = g.Key,
                Total = g.Count(),
                Completed = g.Count(r => r.Status == SigningRequestStatus.Completed),
            })
            .OrderByDescending(x => x.Total)
            .Take(n)
            .Join(
                db.DocumentTemplates,
                outer => outer.TemplateId,
                inner => inner.Id,
                (outer, template) => new
                {
                    templateId = outer.TemplateId,
                    templateName = template.Name,
                    total = outer.Total,
                    completed = outer.Completed,
                    completionRatePercent = outer.Total == 0
                        ? 0.0
                        : Math.Round(outer.Completed * 100.0 / outer.Total, 1),
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new { take = n, items = top });
    }
}
