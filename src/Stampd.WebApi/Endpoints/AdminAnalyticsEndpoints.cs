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
///
/// v2.2 #223 — each endpoint now ALSO runs the same aggregation over the equally-sized
/// prior window (since-2W → since-W) and returns the comparison values alongside the
/// current window. The UI uses these to render "▲ 18% vs prior 30d" style chips so
/// admins see trends, not just snapshots. Two queries per endpoint instead of one;
/// both are index-covered on CreatedAtUtcEpochMs so the cost stays sub-millisecond.
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
            .WithSummary("Invited → Viewed → Signed funnel across the tenant. v2.0 Slice C #207, v2.2 adds prior-window comparison.");

        group.MapGet("/time-to-sign", GetTimeToSignAsync)
            .WithName("GetAdminAnalyticsTimeToSign")
            .WithSummary("Per-template avg + median time from Invited to Signed. v2.0 Slice C #208, v2.2 adds prior-window comparison.");

        group.MapGet("/identity-verification", GetIdentityVerificationAsync)
            .WithName("GetAdminAnalyticsIdentityVerification")
            .WithSummary("Identity-verification challenge / verify / failure counts. v2.0 Slice C #209, v2.2 adds prior-window comparison.");

        group.MapGet("/webhooks-health", GetWebhooksHealthAsync)
            .WithName("GetAdminAnalyticsWebhooksHealth")
            .WithSummary("Webhook endpoint health + recent delivery failures. v2.3 #230.");

        group.MapGet("/by-sender", GetBySenderAsync)
            .WithName("GetAdminAnalyticsBySender")
            .WithSummary("Per-sender productivity: dispatched / completed / avg time-to-sign. v2.3 #231.");

        return builder;
    }

    /// <summary>
    /// Compute the (fromEpochMs, toEpochMsExclusive) pair for the current window AND
    /// the equally-sized prior window. The prior window ends exactly where the current
    /// starts, so each request contributes to at most one — no double-counting.
    /// </summary>
    private static (long CurrentFromMs, long PriorFromMs, long PriorToMsExclusive) WindowEpochs(int windowDays)
    {
        var now = DateTimeOffset.UtcNow;
        var currentFrom = now.AddDays(-windowDays);
        var priorFrom = now.AddDays(-windowDays * 2L);
        return (currentFrom.ToUnixTimeMilliseconds(),
                priorFrom.ToUnixTimeMilliseconds(),
                currentFrom.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Percent change from <paramref name="prior"/> to <paramref name="current"/>.
    /// Returns 0 when prior is 0 to avoid a divide-by-zero blowing up the response;
    /// the UI shows "—" for that case rather than "+∞%".
    /// </summary>
    private static double DeltaPercent(double current, double prior) =>
        prior == 0 ? 0.0 : Math.Round((current - prior) * 100.0 / prior, 1);

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
        var (currentFromMs, priorFromMs, priorToMsExclusive) = WindowEpochs(window);

        // Run the same aggregation against current + prior windows. Both queries are
        // index-covered on (TenantId, CreatedAtUtcEpochMs) so the second call is
        // essentially free compared to the .NET-side materialization.
        var current = await ComputeFunnelAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var prior = await ComputeFunnelAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);

        // v2.2 #225 — request-weighted completion. The recipient-level funnel above
        // is honest about per-person flow but loses request-level nuance: a 2-of-3
        // signed request reads as 2 signed / 3 invited there, while at the request
        // level it's "67% complete". Both views are useful — the UI shows them side
        // by side.
        var currentWeighted = await ComputeWeightedRequestFunnelAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var priorWeighted = await ComputeWeightedRequestFunnelAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);

        // v2.3 #229 — weekday vs weekend split. Tells us whether weekend dispatches
        // convert at the same rate as weekday ones (often they don't — recipients
        // who get an email Saturday morning behave differently than a Monday-morning
        // arrival). Single materialization with day-of-week computed client-side,
        // since DateTimeOffset.DayOfWeek doesn't translate uniformly across providers.
        var byDayBucket = await ComputeFunnelByDayBucketAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            windowDays = window,
            total = current.Total,
            stages = new
            {
                invited = current.Invited,
                viewed = current.Viewed,
                signed = current.Signed,
                declined = current.Declined,
                expired = current.Expired,
            },
            dropOff = new
            {
                invitedToViewedPercent = DropOffPct(current.Invited, current.Viewed),
                viewedToSignedPercent = DropOffPct(current.Viewed, current.Signed),
            },
            conversionRatePercent = current.Invited == 0 ? 0.0 : Math.Round(current.Signed * 100.0 / current.Invited, 1),
            // v2.2 #223 — prior-window comparison. The UI uses these to render delta
            // chips. Keeping the shape symmetric with the current payload so the same
            // record-extraction code can read either side.
            previousWindow = new
            {
                total = prior.Total,
                stages = new
                {
                    invited = prior.Invited,
                    viewed = prior.Viewed,
                    signed = prior.Signed,
                    declined = prior.Declined,
                    expired = prior.Expired,
                },
                conversionRatePercent = prior.Invited == 0 ? 0.0 : Math.Round(prior.Signed * 100.0 / prior.Invited, 1),
            },
            deltas = new
            {
                totalPercent = DeltaPercent(current.Total, prior.Total),
                signedPercent = DeltaPercent(current.Signed, prior.Signed),
                conversionRatePercentPoints = Math.Round(
                    (current.Invited == 0 ? 0.0 : current.Signed * 100.0 / current.Invited) -
                    (prior.Invited == 0 ? 0.0 : prior.Signed * 100.0 / prior.Invited),
                    1),
            },
            // v2.2 #225 — request-weighted view. requestCount is the denominator;
            // weightedCompletionFraction is the request-weighted average of
            // (signed / recipients) per request. fullyCompletedRequests counts the
            // binary "all signed" subset so adopters can compare both notions side by
            // side and pick whichever their internal SLA targets.
            requestWeighted = new
            {
                requestCount = currentWeighted.RequestCount,
                fullyCompletedRequests = currentWeighted.FullyCompletedRequests,
                weightedCompletionFraction = currentWeighted.WeightedCompletionFraction,
                weightedCompletionPercent = Math.Round(currentWeighted.WeightedCompletionFraction * 100.0, 1),
                previousWindow = new
                {
                    requestCount = priorWeighted.RequestCount,
                    fullyCompletedRequests = priorWeighted.FullyCompletedRequests,
                    weightedCompletionPercent = Math.Round(priorWeighted.WeightedCompletionFraction * 100.0, 1),
                },
                deltas = new
                {
                    weightedCompletionPercentPoints = Math.Round(
                        (currentWeighted.WeightedCompletionFraction - priorWeighted.WeightedCompletionFraction) * 100.0,
                        1),
                },
            },
            // v2.3 #229 — weekday vs weekend split. invited/signed broken down by the
            // SigningRequest's CreatedAtUtc day-of-week bucket. UI renders two small
            // funnel mini-charts side by side. Conversion-rate gap between buckets
            // is the actionable metric.
            byDayBucket = new
            {
                weekday = new
                {
                    invited = byDayBucket.Weekday.Invited,
                    signed = byDayBucket.Weekday.Signed,
                    conversionRatePercent = byDayBucket.Weekday.Invited == 0
                        ? 0.0
                        : Math.Round(byDayBucket.Weekday.Signed * 100.0 / byDayBucket.Weekday.Invited, 1),
                },
                weekend = new
                {
                    invited = byDayBucket.Weekend.Invited,
                    signed = byDayBucket.Weekend.Signed,
                    conversionRatePercent = byDayBucket.Weekend.Invited == 0
                        ? 0.0
                        : Math.Round(byDayBucket.Weekend.Signed * 100.0 / byDayBucket.Weekend.Invited, 1),
                },
            },
        });

        // Drop-off percentages between adjacent stages. Each is computed against the
        // earlier stage's count so the UI can render "X% lost between Invited and
        // Viewed." When the earlier stage is 0 we surface 0 instead of NaN.
        static double DropOffPct(int from, int to) =>
            from == 0 ? 0.0 : Math.Round((from - to) * 100.0 / from, 1);
    }

    /// <summary>
    /// One-window funnel aggregation. Filters recipients to SigningRequests whose
    /// epoch is in the half-open interval [fromMs, toMsExclusive). Pass long.MaxValue
    /// for toMsExclusive to mean "no upper bound" (current window's "until now").
    /// </summary>
    private static async Task<FunnelCounts> ComputeFunnelAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var raw = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .GroupBy(_ => 1)
            .Select(g => new FunnelCounts
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

        return raw ?? new FunnelCounts();
    }

    /// <summary>Lightweight value carrier for the funnel-counts projection.</summary>
    private sealed class FunnelCounts
    {
        public int Total { get; init; }
        public int Invited { get; init; }
        public int Viewed { get; init; }
        public int Signed { get; init; }
        public int Declined { get; init; }
        public int Expired { get; init; }
    }

    /// <summary>
    /// Request-weighted completion view. For each SigningRequest in the window we
    /// compute <c>signed / total recipients</c>, then average across requests. A
    /// 2-of-3-signed request contributes 0.67 instead of 0 or 1, so the metric
    /// reflects partial progress on multi-recipient workflows. Requests with zero
    /// recipients are skipped (defensive — shouldn't exist in practice).
    /// </summary>
    private static async Task<WeightedRequestFunnel> ComputeWeightedRequestFunnelAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        // Project just the counts per request — small materialization regardless of
        // recipient count. EF translates this to a single GROUP BY on the recipients
        // table joined to the index-covered SigningRequest predicate.
        var perRequest = await db.SigningRequests
            .Where(r => r.CreatedAtUtcEpochMs >= fromMs
                && r.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                TotalRecipients = r.Recipients.Count,
                SignedRecipients = r.Recipients.Count(rc => rc.Status == RecipientStatus.Signed),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (perRequest.Count == 0)
        {
            return new WeightedRequestFunnel();
        }

        var sumFractions = 0.0;
        var fullyCompleted = 0;
        var contributingRequests = 0;
        foreach (var row in perRequest)
        {
            if (row.TotalRecipients == 0) continue;
            contributingRequests++;
            var fraction = (double)row.SignedRecipients / row.TotalRecipients;
            sumFractions += fraction;
            if (row.SignedRecipients == row.TotalRecipients) fullyCompleted++;
        }

        return new WeightedRequestFunnel
        {
            RequestCount = contributingRequests,
            FullyCompletedRequests = fullyCompleted,
            WeightedCompletionFraction = contributingRequests == 0
                ? 0.0
                : Math.Round(sumFractions / contributingRequests, 4),
        };
    }

    /// <summary>Value carrier for the request-weighted view.</summary>
    private sealed class WeightedRequestFunnel
    {
        public int RequestCount { get; init; }
        public int FullyCompletedRequests { get; init; }
        public double WeightedCompletionFraction { get; init; }
    }

    /// <summary>
    /// v2.3 #229 — bucket recipients by the day-of-week of their SigningRequest's
    /// CreatedAtUtc. Pull just the timestamp + status so the materialization stays
    /// tight; do the dow classification in .NET because DateTimeOffset.DayOfWeek
    /// translation isn't reliable across SQLite/SqlServer/Postgres. Weekday =
    /// Mon-Fri; weekend = Sat-Sun (UTC, not the recipient's local timezone — we
    /// don't have their TZ at dispatch time).
    /// </summary>
    private static async Task<FunnelByDayBucket> ComputeFunnelByDayBucketAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var rows = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                CreatedAtUtc = r.SigningRequest!.CreatedAtUtc,
                InvitedAt = r.InvitedAtUtc,
                Status = r.Status,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var weekdayInvited = 0;
        var weekdaySigned = 0;
        var weekendInvited = 0;
        var weekendSigned = 0;
        foreach (var r in rows)
        {
            var dow = r.CreatedAtUtc.DayOfWeek;
            var isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;
            if (r.InvitedAt != null)
            {
                if (isWeekend) weekendInvited++; else weekdayInvited++;
            }
            if (r.Status == RecipientStatus.Signed)
            {
                if (isWeekend) weekendSigned++; else weekdaySigned++;
            }
        }

        return new FunnelByDayBucket
        {
            Weekday = new DayBucketCounts { Invited = weekdayInvited, Signed = weekdaySigned },
            Weekend = new DayBucketCounts { Invited = weekendInvited, Signed = weekendSigned },
        };
    }

    /// <summary>Value carrier for the day-bucket split.</summary>
    private sealed class FunnelByDayBucket
    {
        public DayBucketCounts Weekday { get; init; } = new();
        public DayBucketCounts Weekend { get; init; } = new();
    }

    private sealed class DayBucketCounts
    {
        public int Invited { get; init; }
        public int Signed { get; init; }
    }

    /// <summary>
    /// Per-template average + median time-to-sign (Invited → Signed gap), top N by
    /// signed recipient count. Average can be computed server-side; median requires
    /// materializing the sorted list per template and picking the midpoint client-side.
    /// We keep the materialize set small via Top N and the window.
    ///
    /// v2.2 #223 — also computes prior-window avg/median per template so the UI can
    /// surface "this template's median is up 12% vs last 30 days". Templates that
    /// appeared in the current window but not the prior get null prior values.
    /// </summary>
    private static async Task<IResult> GetTimeToSignAsync(
        [FromQuery] int? days,
        [FromQuery] int? take,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var n = Math.Clamp(take ?? DefaultTimeToSignTake, 1, MaxTimeToSignTake);
        var (currentFromMs, priorFromMs, priorToMsExclusive) = WindowEpochs(window);

        var currentByTemplate = await ComputeTimeToSignAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var priorByTemplate = await ComputeTimeToSignAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);

        // Top N from current window — the prior values get joined in by templateId.
        var topCurrent = currentByTemplate
            .OrderByDescending(x => x.SignedCount)
            .Take(n)
            .ToList();

        var templateIds = topCurrent.Select(x => x.TemplateId).ToList();
        var nameByTemplate = await db.DocumentTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var nameLookup = nameByTemplate.ToDictionary(x => x.Id, x => x.Name);
        var priorLookup = priorByTemplate.ToDictionary(x => x.TemplateId);

        var items = topCurrent
            .Select(x =>
            {
                priorLookup.TryGetValue(x.TemplateId, out var p);
                return new
                {
                    templateId = x.TemplateId,
                    templateName = nameLookup.GetValueOrDefault(x.TemplateId, "(unknown)"),
                    signedCount = x.SignedCount,
                    avgMinutes = x.AvgMinutes,
                    medianMinutes = x.MedianMinutes,
                    // v2.2 #223 — nullable so the UI can render "—" for templates new
                    // in the current window. Delta is null when there's no prior baseline.
                    previousWindow = p is null ? null : new
                    {
                        signedCount = p.SignedCount,
                        avgMinutes = p.AvgMinutes,
                        medianMinutes = p.MedianMinutes,
                    },
                    avgMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(x.AvgMinutes, p.AvgMinutes),
                    medianMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(x.MedianMinutes, p.MedianMinutes),
                };
            })
            .ToList();

        // v2.2 #226 — per-role aggregation across templates. Surfaces "do Approvers
        // always take longer than Signers?" at the tenant level. Recipient.Role is
        // a template-level name (e.g. "Customer", "Witness", "Manager"), not the
        // RBAC role from StampdRoles — that one's on the dispatching user, not the
        // recipient. We bucket by role NAME so semantically-equivalent roles across
        // templates get aggregated (a "Customer" role on Template A and a "Customer"
        // role on Template B share the same name; their recipient cohorts merge).
        var byRoleCurrent = await ComputeTimeToSignByRoleAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var byRolePrior = await ComputeTimeToSignByRoleAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);
        var byRolePriorLookup = byRolePrior.ToDictionary(x => x.RoleName, StringComparer.OrdinalIgnoreCase);

        var byRole = byRoleCurrent
            .OrderByDescending(r => r.SignedCount)
            .Select(r =>
            {
                byRolePriorLookup.TryGetValue(r.RoleName, out var p);
                return new
                {
                    roleName = r.RoleName,
                    signedCount = r.SignedCount,
                    avgMinutes = r.AvgMinutes,
                    medianMinutes = r.MedianMinutes,
                    previousWindow = p is null ? null : new
                    {
                        signedCount = p.SignedCount,
                        avgMinutes = p.AvgMinutes,
                        medianMinutes = p.MedianMinutes,
                    },
                    avgMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(r.AvgMinutes, p.AvgMinutes),
                    medianMinutesDeltaPercent = p is null ? (double?)null : DeltaPercent(r.MedianMinutes, p.MedianMinutes),
                };
            })
            .ToList();

        return Results.Ok(new { windowDays = window, take = n, items, byRole });
    }

    /// <summary>
    /// Per-role time-to-sign aggregation. Roles with no name (legacy data with
    /// Recipient.RoleId = null) bucket under "(unassigned)" so the row count adds
    /// back to the recipient-level total.
    /// </summary>
    private static async Task<List<TimeToSignRoleRow>> ComputeTimeToSignByRoleAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var rows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                RoleName = r.Role != null ? r.Role.Name : null,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.RoleName) ? "(unassigned)" : r.RoleName!,
                StringComparer.OrdinalIgnoreCase)
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
                return new TimeToSignRoleRow
                {
                    RoleName = g.Key,
                    SignedCount = durations.Count,
                    AvgMinutes = Math.Round(avg, 1),
                    MedianMinutes = Math.Round(median, 1),
                };
            })
            .ToList();
    }

    /// <summary>Per-role time-to-sign projection.</summary>
    private sealed class TimeToSignRoleRow
    {
        public string RoleName { get; init; } = string.Empty;
        public int SignedCount { get; init; }
        public double AvgMinutes { get; init; }
        public double MedianMinutes { get; init; }
    }

    /// <summary>
    /// One-window per-template time-to-sign aggregation. Returns one row per template
    /// that had at least one signed recipient in the window.
    /// </summary>
    private static async Task<List<TimeToSignRow>> ComputeTimeToSignAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var rows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .Select(r => new
            {
                TemplateId = r.SigningRequest!.DocumentTemplateId,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
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
                return new TimeToSignRow
                {
                    TemplateId = g.Key,
                    SignedCount = durations.Count,
                    AvgMinutes = Math.Round(avg, 1),
                    MedianMinutes = Math.Round(median, 1),
                };
            })
            .ToList();
    }

    /// <summary>Per-template time-to-sign projection used by both windows.</summary>
    private sealed class TimeToSignRow
    {
        public Guid TemplateId { get; init; }
        public int SignedCount { get; init; }
        public double AvgMinutes { get; init; }
        public double MedianMinutes { get; init; }
    }

    /// <summary>
    /// Identity-verification rollup: how many recipients required IV, how many
    /// succeeded, how many failed (locked out per the v1.3 #136 max-attempts), and
    /// the per-window verify rate. Joins audit events to recipients so we attribute
    /// IV stats to the recipient's role (which carried RequiresIdentityVerification).
    ///
    /// v2.2 #223 — adds prior-window comparison alongside current values.
    /// v2.2 #224 — adds per-channel breakdown (Email / SMS / KBA) so admins can spot
    /// which auth method is being brute-forced.
    /// </summary>
    private static async Task<IResult> GetIdentityVerificationAsync(
        [FromQuery] int? days,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var (currentFromMs, priorFromMs, priorToMsExclusive) = WindowEpochs(window);

        var current = await ComputeIdentityVerificationAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);
        var prior = await ComputeIdentityVerificationAsync(db, priorFromMs, priorToMsExclusive, ct).ConfigureAwait(false);
        var byChannel = await ComputeIdentityVerificationByChannelAsync(db, currentFromMs, long.MaxValue, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            windowDays = window,
            requiredCount = current.RequiredCount,
            verifiedCount = current.VerifiedCount,
            verifyRatePercent = current.VerifyRatePercent,
            initiatesCount = current.InitiatesCount,
            lockedOutCount = current.LockedOutCount,
            previousWindow = new
            {
                requiredCount = prior.RequiredCount,
                verifiedCount = prior.VerifiedCount,
                verifyRatePercent = prior.VerifyRatePercent,
                initiatesCount = prior.InitiatesCount,
                lockedOutCount = prior.LockedOutCount,
            },
            deltas = new
            {
                requiredCountPercent = DeltaPercent(current.RequiredCount, prior.RequiredCount),
                verifiedCountPercent = DeltaPercent(current.VerifiedCount, prior.VerifiedCount),
                // Rate is already a percent, so the delta is "percentage points"
                // not "percent change". UI labels distinguish the two.
                verifyRatePercentPoints = Math.Round(current.VerifyRatePercent - prior.VerifyRatePercent, 1),
                initiatesCountPercent = DeltaPercent(current.InitiatesCount, prior.InitiatesCount),
                lockedOutCountPercent = DeltaPercent(current.LockedOutCount, prior.LockedOutCount),
            },
            // v2.2 #224 — per-channel breakdown. Lockout column shows which method is
            // taking the brute-force volume; verifies surface KBA usage which doesn't
            // flow through the OTP store. The UI renders a small stacked bar from this.
            byChannel = new
            {
                email = new
                {
                    initiatesCount = byChannel.Email.InitiatesCount,
                    verifiedCount = byChannel.Email.VerifiedCount,
                    lockedOutCount = byChannel.Email.LockedOutCount,
                },
                sms = new
                {
                    initiatesCount = byChannel.Sms.InitiatesCount,
                    verifiedCount = byChannel.Sms.VerifiedCount,
                    lockedOutCount = byChannel.Sms.LockedOutCount,
                },
                kba = new
                {
                    initiatesCount = byChannel.Kba.InitiatesCount,
                    verifiedCount = byChannel.Kba.VerifiedCount,
                    lockedOutCount = byChannel.Kba.LockedOutCount,
                },
            },
        });
    }

    /// <summary>
    /// Classify an OTP challenge's identifier as Email / SMS based on shape. The OTP
    /// store doesn't carry a Channel column — adding one would mean a V16 migration
    /// for a single derived field. The identifier itself is unambiguous in practice:
    /// EmailOtpProvider hands us an email address, SmsOtpProvider hands us a phone
    /// number, and the two character classes don't overlap. KBA bypasses the OTP
    /// store entirely, so any OTP row is Email or SMS by construction.
    /// </summary>
    private static string ClassifyOtpChannel(string identifier) =>
        identifier?.Contains('@', StringComparison.Ordinal) == true ? "Email" : "Sms";

    /// <summary>
    /// Per-channel IV aggregation for the current window. Pulls the per-channel
    /// slices in a single materialization each so the .NET-side grouping stays
    /// O(rows in window) and the SQL stays index-covered.
    /// </summary>
    private static async Task<IdentityVerificationByChannel> ComputeIdentityVerificationByChannelAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs);
        var to = toMsExclusive == long.MaxValue
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeMilliseconds(toMsExclusive);

        // Pull just the identifier + failed-attempts pair per OTP challenge — small
        // projection, .NET-side classification by ClassifyOtpChannel. Materialization
        // is bounded by the window so this stays cheap.
        const int lockoutThreshold = 5;
        var otpRows = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtc >= from && c.CreatedAtUtc < to)
            .Select(c => new { c.Identifier, c.FailedAttempts })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var emailInitiates = 0;
        var emailLockedOut = 0;
        var smsInitiates = 0;
        var smsLockedOut = 0;
        foreach (var row in otpRows)
        {
            var channel = ClassifyOtpChannel(row.Identifier);
            if (channel == "Email")
            {
                emailInitiates++;
                if (row.FailedAttempts >= lockoutThreshold) emailLockedOut++;
            }
            else
            {
                smsInitiates++;
                if (row.FailedAttempts >= lockoutThreshold) smsLockedOut++;
            }
        }

        // Verified counts pivot on Recipient.IdentityVerificationMethod which is
        // stamped by the provider after a successful verify. EmailOtpProvider writes
        // "EmailOtp", SmsOtpProvider writes "Sms", KbaProvider writes "Kba" — exact
        // case-sensitive strings, so the WHERE is safe to translate.
        var verifiedByMethod = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive
                && r.IdentityVerifiedAtUtc != null
                && r.IdentityVerificationMethod != null)
            .GroupBy(r => r.IdentityVerificationMethod!)
            .Select(g => new { Method = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int VerifiedCount(string method) =>
            verifiedByMethod.FirstOrDefault(x =>
                string.Equals(x.Method, method, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;

        // KBA doesn't flow through OTP challenges — initiate count comes from
        // recipients whose role required IV and who verified via KBA. We can't
        // distinguish "initiated but failed" from "never started" without a KBA-
        // specific attempt log, so KBA initiates == KBA verifieds for now and
        // lockedOutCount stays 0. Documented intentionally so adopters see the
        // limitation rather than a wrong number.
        var kbaVerified = VerifiedCount("Kba");

        return new IdentityVerificationByChannel
        {
            Email = new IdentityVerificationChannelCounts
            {
                InitiatesCount = emailInitiates,
                VerifiedCount = VerifiedCount("EmailOtp"),
                LockedOutCount = emailLockedOut,
            },
            Sms = new IdentityVerificationChannelCounts
            {
                InitiatesCount = smsInitiates,
                VerifiedCount = VerifiedCount("Sms"),
                LockedOutCount = smsLockedOut,
            },
            Kba = new IdentityVerificationChannelCounts
            {
                InitiatesCount = kbaVerified,
                VerifiedCount = kbaVerified,
                LockedOutCount = 0,
            },
        };
    }

    /// <summary>Value carrier for the per-channel breakdown.</summary>
    private sealed class IdentityVerificationByChannel
    {
        public IdentityVerificationChannelCounts Email { get; init; } = new();
        public IdentityVerificationChannelCounts Sms { get; init; } = new();
        public IdentityVerificationChannelCounts Kba { get; init; } = new();
    }

    private sealed class IdentityVerificationChannelCounts
    {
        public int InitiatesCount { get; init; }
        public int VerifiedCount { get; init; }
        public int LockedOutCount { get; init; }
    }

    /// <summary>
    /// One-window IV aggregation. The OTP-challenge counts use the raw
    /// <c>CreatedAtUtc</c> column (no epoch shadow on OtpChallengeEntity yet — it's a
    /// short-lived row so the column isn't a hot-path issue), wrapped in the same
    /// half-open interval as the recipient/audit queries.
    /// </summary>
    private static async Task<IdentityVerificationCounts> ComputeIdentityVerificationAsync(
        StampdDbContext db, long fromMs, long toMsExclusive, CancellationToken ct)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs);
        var to = toMsExclusive == long.MaxValue
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeMilliseconds(toMsExclusive);

        var requiredCount = await db.Recipients
            .Where(r => r.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && r.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive
                && r.Role != null
                && r.Role.RequiresIdentityVerification)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var verifiedCount = await db.AuditEvents
            .Where(e => e.EventType == AuditEventType.RecipientIdentityVerified
                && e.SigningRequest!.CreatedAtUtcEpochMs >= fromMs
                && e.SigningRequest.CreatedAtUtcEpochMs < toMsExclusive)
            .CountAsync(ct)
            .ConfigureAwait(false);

        const int lockoutThreshold = 5;
        var lockedOutCount = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtc >= from && c.CreatedAtUtc < to && c.FailedAttempts >= lockoutThreshold)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var initiatesCount = await db.Set<Stampd.Infrastructure.Identity.OtpChallengeEntity>()
            .Where(c => c.CreatedAtUtc >= from && c.CreatedAtUtc < to)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var verifyRate = requiredCount == 0
            ? 0.0
            : Math.Round(verifiedCount * 100.0 / requiredCount, 1);

        return new IdentityVerificationCounts
        {
            RequiredCount = requiredCount,
            VerifiedCount = verifiedCount,
            VerifyRatePercent = verifyRate,
            InitiatesCount = initiatesCount,
            LockedOutCount = lockedOutCount,
        };
    }

    /// <summary>Value carrier for the IV aggregation.</summary>
    private sealed class IdentityVerificationCounts
    {
        public int RequiredCount { get; init; }
        public int VerifiedCount { get; init; }
        public double VerifyRatePercent { get; init; }
        public int InitiatesCount { get; init; }
        public int LockedOutCount { get; init; }
    }

    // ---------------------------------------------------------------------
    // v2.3 #230 — webhook delivery retry observability
    // ---------------------------------------------------------------------

    /// <summary>Top-N for the recent-failures table. Keeps the response bounded.</summary>
    private const int WebhooksRecentFailuresTake = 10;

    /// <summary>
    /// Webhook health rollup. Surfaces total + active + degraded endpoints, the
    /// retry-queue depth, and the most recent failed deliveries — everything an
    /// admin needs to spot a broken subscriber without opening server logs. All
    /// counts are tenant-scoped (the WebhookEndpoint / WebhookDelivery query filters
    /// in StampdDbContext do the tenant filtering for us).
    /// </summary>
    private static async Task<IResult> GetWebhooksHealthAsync(
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var oneDayAgo = now.AddDays(-1);

        // Endpoint-level snapshot. ConsecutiveFailures > 0 is the "this endpoint is
        // currently flapping" signal — we report it as "degraded" rather than
        // "failed" because the worker may still recover on the next attempt.
        var endpoints = await db.WebhookEndpoints
            .Select(e => new
            {
                e.Id,
                e.Url,
                e.IsActive,
                e.ConsecutiveFailures,
                e.LastDeliveryAttemptAtUtc,
                e.LastSuccessAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalEndpoints = endpoints.Count;
        var activeEndpoints = endpoints.Count(e => e.IsActive);
        var degradedEndpoints = endpoints.Count(e => e.IsActive && e.ConsecutiveFailures > 0);

        // Retry-queue depth: deliveries scheduled to run later. AttemptCount > 0
        // means they've already failed at least once. Useful to distinguish a
        // healthy queue (lots of new deliveries) from a backed-up retry storm.
        var pendingDeliveries = await db.WebhookDeliveries
            .Where(d => d.NextAttemptAtUtc > now)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var retryingDeliveries = await db.WebhookDeliveries
            .Where(d => d.AttemptCount > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Last 24h activity. The outbox worker deletes on success, so a row that
        // exists with AttemptCount > 0 and a populated LastErrorMessage is what
        // "currently failed" looks like in this design.
        var failuresLast24h = await db.WebhookDeliveries
            .Where(d => d.CreatedAtUtc >= oneDayAgo && d.AttemptCount > 0)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Most recent failed deliveries with endpoint context. Pull a small fixed
        // top-N so the table renders cleanly on the dashboard.
        var recentFailures = await db.WebhookDeliveries
            .Where(d => d.AttemptCount > 0 && d.LastErrorMessage != null)
            .OrderByDescending(d => d.NextAttemptAtUtcEpochMs)
            .Take(WebhooksRecentFailuresTake)
            .Select(d => new
            {
                d.Id,
                endpointUrl = d.WebhookEndpoint!.Url,
                eventType = d.EventType.ToString(),
                attemptCount = d.AttemptCount,
                lastResponseStatus = d.LastResponseStatus,
                lastErrorMessage = d.LastErrorMessage,
                nextAttemptAtUtc = d.NextAttemptAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            totalEndpoints,
            activeEndpoints,
            degradedEndpoints,
            pendingDeliveries,
            retryingDeliveries,
            failuresLast24h,
            recentFailures,
        });
    }

    // ---------------------------------------------------------------------
    // v2.3 #231 — per-sender productivity
    // ---------------------------------------------------------------------

    private const int DefaultBySenderTake = 10;
    private const int MaxBySenderTake = 50;

    /// <summary>
    /// Per-sender (i.e. per <c>SigningRequest.CreatedBy</c>) dispatched / completed /
    /// voided counts plus avg time-to-sign for the sender's workflows in the window.
    /// Lets admins spot which senders are productive vs. which are sending dead-end
    /// requests. CreatedBy is a string identifier (typically the JWT <c>sub</c>); we
    /// surface it as-is and let the UI handle display.
    /// </summary>
    private static async Task<IResult> GetBySenderAsync(
        [FromQuery] int? days,
        [FromQuery] int? take,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var n = Math.Clamp(take ?? DefaultBySenderTake, 1, MaxBySenderTake);
        var (currentFromMs, _, _) = WindowEpochs(window);

        // Group on the indexed CreatedBy + epoch column. EF translates this to a
        // single SELECT with a GROUP BY — no .NET-side materialization beyond the
        // grouped projection.
        var grouped = await db.SigningRequests
            .Where(r => r.CreatedAtUtcEpochMs >= currentFromMs)
            .GroupBy(r => r.CreatedBy)
            .Select(g => new
            {
                CreatedBy = g.Key,
                Dispatched = g.Count(),
                Completed = g.Count(r => r.Status == SigningRequestStatus.Completed),
                Voided = g.Count(r => r.Status == SigningRequestStatus.Voided),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (grouped.Count == 0)
        {
            return Results.Ok(new { windowDays = window, take = n, items = Array.Empty<object>() });
        }

        // Compute per-sender avg time-to-sign by pulling the signed-recipient
        // timestamps in a second query. Same pattern as time-to-sign — duration
        // arithmetic in .NET because DateTimeOffset translation isn't uniform.
        var senderIds = grouped.Select(g => g.CreatedBy).ToList();
        var signedRows = await db.Recipients
            .Where(r => r.Status == RecipientStatus.Signed
                && r.InvitedAtUtc != null
                && r.SignedAtUtc != null
                && r.SigningRequest!.CreatedAtUtcEpochMs >= currentFromMs
                && senderIds.Contains(r.SigningRequest.CreatedBy))
            .Select(r => new
            {
                Sender = r.SigningRequest!.CreatedBy,
                r.InvitedAtUtc,
                r.SignedAtUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var avgBySender = signedRows
            .GroupBy(r => r.Sender)
            .ToDictionary(
                g => g.Key,
                g => Math.Round(g.Average(r => (r.SignedAtUtc!.Value - r.InvitedAtUtc!.Value).TotalMinutes), 1));

        var items = grouped
            .OrderByDescending(g => g.Dispatched)
            .Take(n)
            .Select(g => new
            {
                sender = g.CreatedBy,
                dispatched = g.Dispatched,
                completed = g.Completed,
                voided = g.Voided,
                completionRatePercent = g.Dispatched == 0
                    ? 0.0
                    : Math.Round(g.Completed * 100.0 / g.Dispatched, 1),
                avgTimeToSignMinutes = avgBySender.GetValueOrDefault(g.CreatedBy, 0.0),
            })
            .ToList();

        return Results.Ok(new { windowDays = window, take = n, items });
    }
}
