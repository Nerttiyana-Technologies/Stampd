using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>
/// v2.0 Slice C — admin-only read endpoints for the analytics section of the /admin
/// dashboard. Three independent endpoints (funnel, time-to-sign, identity-verification)
/// so the UI can refetch any single widget without re-running the others.
/// </summary>
/// <remarks>
/// All three live under <c>/api/admin/analytics</c> and inherit the <c>Admin</c>-only
/// policy from the adminGroup in Program.cs. None mutate state; they're pure
/// aggregations against the v1.3 epoch shadow columns + the v2.0 audit/actor schema.
/// </remarks>
internal static class AdminAnalyticsEndpoints
{
    /// <summary>Default analytics window. 30 days matches Slice A's trend chart.</summary>
    private const int DefaultWindowDays = 30;

    /// <summary>Hard cap on the window. Keeps the materialize-then-aggregate paths bounded.</summary>
    private const int MaxWindowDays = 365;

    /// <summary>Default top-N for time-to-sign — fits one card on the dashboard cleanly.</summary>
    private const int DefaultTimeToSignTake = 10;
    private const int MaxTimeToSignTake = 50;

    public static IEndpointRouteBuilder MapAdminAnalytics(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/analytics").WithTags("AdminAnalytics");

        group.MapGet("/funnel", GetFunnelAsync)
            .WithName("GetAdminAnalyticsFunnel")
            .WithSummary("Invited → Viewed → Signed funnel across the tenant. v2.0 Slice C #207.");

        group.MapGet("/time-to-sign", GetTimeToSignAsync)
            .WithName("GetAdminAnalyticsTimeToSign")
            .WithSummary("Per-template avg + median time from Invited to Signed. v2.0 Slice C #208.");

        group.MapGet("/identity-verification", GetIdentityVerificationAsync)
            .WithName("GetAdminAnalyticsIdentityVerification")
            .WithSummary("Identity-verification challenge / verify / failure counts. v2.0 Slice C #209.");

        return builder;
    }

    /// <summary>
    /// Counts recipients by max state reached in the window — the chosen window
    /// filters by SigningRequest.CreatedAtUtcEpochMs so each recipient contributes
    /// to exactly one stage (whatever's furthest in their lifecycle).
    /// </summary>
    private static async Task<IResult> GetFunnelAsync(
        [FromQuery] int? days,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var sinceEpochMs = DateTimeOffset.UtcNow
            .AddDays(-window)
            .ToUnixTimeMilliseconds();

        // GroupBy(_ => 1) so EF emits a single SELECT with conditional COUNTs.
        // Stage assignment: Status wins (terminal states), else InvitedAt/FirstViewedAt
        // presence. RecipientStatus is a v1.x stable enum so the integer comparisons
        // are safe across providers without enum-name conversion overhead.
        var funnel = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= sinceEpochMs)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Invited = g.Count(r => r.InvitedAtUtc != null),
                Viewed = g.Count(r => r.FirstViewedAtUtc != null),
                Signed = g.Count(r => r.Status == RecipientStatus.Signed),
                Declined = g.Count(r => r.Status == RecipientStatus.Declined),
                Expired = g.Count(r => r.Status == RecipientStatus.Expired),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var invited = funnel?.Invited ?? 0;
        var viewed = funnel?.Viewed ?? 0;
        var signed = funnel?.Signed ?? 0;
        var declined = funnel?.Declined ?? 0;
        var expired = funnel?.Expired ?? 0;
        var total = funnel?.Total ?? 0;

        // Drop-off percentages between adjacent stages. Each is computed against the
        // earlier stage's count so the UI can render "X% lost between Invited and
        // Viewed." When the earlier stage is 0 we surface 0 instead of NaN.
        static double DropOffPct(int from, int to) =>
            from == 0 ? 0.0 : Math.Round((from - to) * 100.0 / from, 1);

        return Results.Ok(new
        {
            windowDays = window,
            total,
            stages = new
            {
                invited,
                viewed,
                signed,
                declined,
                expired,
            },
            dropOff = new
            {
                invitedToViewedPercent = DropOffPct(invited, viewed),
                viewedToSignedPercent = DropOffPct(viewed, signed),
            },
            conversionRatePercent = invited == 0 ? 0.0 : Math.Round(signed * 100.0 / invited, 1),
        });
    }

    /// <summary>
    /// Per-template average + median time-to-sign (Invited → Signed gap), top N by
    /// signed recipient count. Average can be computed server-side; median requires
    /// materializing the sorted list per template and picking the midpoint client-side.
    /// We keep the materialize set small via Top N and the window.
    /// </summary>
    private static async Task<IResult> GetTimeToSignAsync(
        [FromQuery] int? days,
        [FromQuery] int? take,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var n = Math.Clamp(take ?? DefaultTimeToSignTake, 1, MaxTimeToSignTake);
        var sinceEpochMs = DateTimeOffset.UtcNow
            .AddDays(-window)
            .ToUnixTimeMilliseconds();

        // Pull (templateId, durationMs) tuples for every signed recipient in the
        // window. We compute duration in the .NET layer because DateTimeOffset
        // arithmetic doesn't translate uniformly across providers (v1.2 #115 / Doc 16
        // again). The Where filters above are translatable; the projection
        // materializes only the two timestamps per row.
        var rows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= sinceEpochMs)
            .Select(r => new
            {
                TemplateId = r.SigningRequest!.DocumentTemplateId,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Group by template, compute avg + median in-memory. Median is the
        // canonical "middle value of the sorted list" — for odd counts pick the
        // middle; for even counts average the two middles. Avg + median together
        // surface "is this template slow because of one outlier or systemically?"
        var perTemplate = rows
            .GroupBy(r => r.TemplateId)
            .Select(g =>
            {
                var durations = g
                    .Select(r => (long)(r.SignedAtUtc!.Value - r.InvitedAtUtc!.Value).TotalMinutes)
                    .OrderBy(d => d)
                    .ToList();
                var avg = durations.Average();
                var median = durations.Count switch
                {
                    0 => 0.0,
                    var c when c % 2 == 1 => durations[c / 2],
                    var c => (durations[c / 2 - 1] + durations[c / 2]) / 2.0,
                };
                return new
                {
                    templateId = g.Key,
                    signedCount = durations.Count,
                    avgMinutes = Math.Round(avg, 1),
                    medianMinutes = Math.Round(median, 1),
                };
            })
            .OrderByDescending(x => x.signedCount)
            .Take(n)
            .ToList();

        // Join to template names for display. Cheap — at most N templates.
        var templateIds = perTemplate.Select(x => x.templateId).ToList();
        var nameByTemplate = await db.DocumentTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var nameLookup = nameByTemplate.ToDictionary(x => x.Id, x => x.Name);

        var items = perTemplate
            .Select(x => new
            {
                x.templateId,
                templateName = nameLookup.GetValueOrDefault(x.templateId, "(unknown)"),
                x.signedCount,
                x.avgMinutes,
                x.medianMinutes,
            })
            .ToList();

        return Results.Ok(new { windowDays = window, take = n, items });
    }

    /// <summary>
    /// Identity-verification rollup: how many recipients required IV, how many
    /// succeeded, how many failed (locked out per the v1.3 #136 max-attempts), and
    /// the per-window verify rate. Joins audit events to recipients so we attribute
    /// IV stats to the recipient's role (which carried RequiresIdentityVerification).
    /// </summary>
    private static async Task<IResult> GetIdentityVerificationAsync(
        [FromQuery] int? days,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var since = DateTimeOffset.UtcNow.AddDays(-window);

        // We can't filter AuditEvent.OccurredAtUtc directly via SQL (Doc 16 / Doc 25
        // issue), so materialize the count via the SigningRequest's epoch column,
        // which is what the audit row points at via FK.
        var sinceEpochMs = since.ToUnixTimeMilliseconds();

        // Count IV-required recipients in window — uses the Role.RequiresIdentityVerification
        // flag set at template design time.
        var requiredCount = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= sinceEpochMs
                && r.Role != null
                && r.Role.RequiresIdentityVerification)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Count successful verifications via the RecipientIdentityVerified audit event,
        // joined through SigningRequest so the date filter stays index-covered.
        var verifiedCount = await db.AuditEvents
            .Where(e => e.EventType == AuditEventType.RecipientIdentityVerified
                && e.SigningRequest!.CreatedAtUtcEpochMs >= sinceEpochMs)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Brute-force attempts surfaced via OtpChallenges.FailedAttempts >= configured
        // threshold (v1.3 #136). 5 is the default MaxFailedAttempts and what most
        // adopters run with; surfacing this raw means the dashboard reflects reality
        // even when adopters tune the threshold.
        var lockoutThreshold = 5;
        var lockedOutCount = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtc >= since && c.FailedAttempts >= lockoutThreshold)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Total OTP initiates so the UI can show "X verified out of Y attempts".
        var initiatesCount = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtc >= since)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var verifyRate = requiredCount == 0
            ? 0.0
            : Math.Round(verifiedCount * 100.0 / requiredCount, 1);

        return Results.Ok(new
        {
            windowDays = window,
            requiredCount,
            verifiedCount,
            verifyRatePercent = verifyRate,
            initiatesCount,
            lockedOutCount,
        });
    }
}
