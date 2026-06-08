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

        group.MapGet("/", ListAsync)
            .WithName("ListSigningRequests")
            .WithSummary("Lists signing requests for the current tenant, newest first. Optionally filter by templateId.");

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
        [FromServices] WorkflowEmailOptions emailOptions,
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
            ToResponse(created, http, emailOptions));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        [FromServices] WorkflowEmailOptions emailOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var request = await db.SigningRequests
            .Include(r => r.Recipients).ThenInclude(p => p.Role)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        return request is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(request, http, emailOptions));
    }

    /// <summary>
    /// Defaults align with the Blazor list page: 25 rows per page hits the sweet spot
    /// between scannable density and one-screen visibility. The 200 cap matches the
    /// historical hard limit at <c>Take(100)</c> headroom so power users can pull
    /// generous slices without DoS risk on the (TenantId, CreatedAtUtcEpochMs) index.
    /// </summary>
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;

    private static async Task<IResult> ListAsync(
        [FromQuery] Guid? templateId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        // v2.0 Slice B — filter params (all optional, all additive).
        [FromQuery] string? status,
        [FromQuery] string? senderEmail,
        [FromQuery] string? recipientEmail,
        [FromQuery] DateTimeOffset? dispatchedFrom,
        [FromQuery] DateTimeOffset? dispatchedTo,
        [FromQuery] string? sortBy,
        [FromQuery] string? direction,
        [FromServices] StampdDbContext db,
        [FromServices] WorkflowEmailOptions emailOptions,
        HttpContext http,
        CancellationToken ct)
    {
        // Coerce to safe bounds. Defensive against negative or absurdly large query
        // values from over-zealous callers — the page object always returns a valid
        // page even when the caller asks for page 99 of a 3-page result.
        var requestedPageSize = pageSize.GetValueOrDefault(DefaultPageSize);
        var effectivePageSize = requestedPageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => requestedPageSize,
        };
        var effectivePage = Math.Max(1, page.GetValueOrDefault(1));

        var query = db.SigningRequests.AsQueryable();

        // ---- Filters ----
        if (templateId is not null)
        {
            query = query.Where(r => r.DocumentTemplateId == templateId.Value);
        }

        // status accepts a comma-separated list so the UI's multi-select chip group
        // can pack multiple statuses into one query string. Empty / invalid entries
        // are silently dropped — an unknown status doesn't fail the request.
        if (!string.IsNullOrWhiteSpace(status))
        {
            // Materialize as List<T> not T[]. In C# 14, T[].Contains binds to the
            // ReadOnlySpan<T>.Contains extension (MemoryExtensions) instead of the
            // Enumerable.Contains extension, which (a) changes equality semantics
            // (IEquatable<T> vs IEqualityComparer) and (b) breaks EF Core's
            // recognition pattern for translating to SQL IN (...). List<T>.Contains
            // is an unambiguous instance method that both compilers and EF Core
            // translate identically to IN.
            var statuses = status
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<SigningRequestStatus>(s, ignoreCase: true, out var parsed)
                    ? (SigningRequestStatus?)parsed
                    : null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToList();

            if (statuses.Count > 0)
            {
                // EF Core translates List<T>.Contains(entity.Property) to SQL IN (...).
                query = query.Where(r => statuses.Contains(r.Status));
            }
        }

        // senderEmail / recipientEmail use exact-match (case-sensitive) for the same
        // EF Core 10 SQLite reason as v1.3 #136 — string.ToLower() doesn't translate
        // portably. The UI auto-lowercases when storing CreatedBy / Recipient.Email so
        // exact match is correct in practice.
        if (!string.IsNullOrWhiteSpace(senderEmail))
        {
            var sender = senderEmail.Trim();
            query = query.Where(r => r.CreatedBy == sender);
        }

        if (!string.IsNullOrWhiteSpace(recipientEmail))
        {
            var recip = recipientEmail.Trim();
            // EF translates Any on a nav collection to EXISTS — server-side, no Recipients
            // hydration cost.
            query = query.Where(r => r.Recipients.Any(rc => rc.Email == recip));
        }

        // Date range on the epoch shadow column (same v1.3 #133 trick that makes the
        // newest-first ORDER BY index-covered). Compare against the supplied UTC
        // boundary's epoch ms; for the "to" side we extend to end-of-day so a caller
        // passing 2026-06-30 gets all of that day, not just midnight.
        if (dispatchedFrom is not null)
        {
            var fromMs = dispatchedFrom.Value.ToUnixTimeMilliseconds();
            query = query.Where(r => r.CreatedAtUtcEpochMs >= fromMs);
        }

        if (dispatchedTo is not null)
        {
            var toEndOfDay = dispatchedTo.Value.UtcDateTime.Date.AddDays(1).AddTicks(-1);
            var toMs = new DateTimeOffset(toEndOfDay, TimeSpan.Zero).ToUnixTimeMilliseconds();
            query = query.Where(r => r.CreatedAtUtcEpochMs <= toMs);
        }

        // Count first (cheap on the indexed column) so we can return totalPages even
        // when the requested page is empty. Run on the bare query before Include —
        // EF translates this to SELECT COUNT(*) which doesn't need the joins.
        var total = await query.CountAsync(ct).ConfigureAwait(false);

        // ---- Sort ----
        // sortBy defaults to "dispatched" (v1.3 #158's newest-first); direction defaults
        // to "desc". Unknown values fall back to defaults silently — over-tolerance is
        // better than a 400 here because a stale UI bookmark shouldn't break the page.
        var normalizedSort = (sortBy ?? "dispatched").ToLowerInvariant();
        var ascending = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);

        IQueryable<SigningRequest> orderedQuery = normalizedSort switch
        {
            // v2.1 #212 — strict completed-date sort, backed by the V15
            // (TenantId, CompletedAtUtcEpochMs) covering index. In-flight workflows
            // have a null shadow; we push them to the trailing bucket regardless of
            // direction so the visible page always leads with concrete completion
            // dates and the user doesn't see a wall of nulls at the top of "desc".
            // The ThenBy on CreatedAtUtcEpochMs gives a stable tiebreak.
            "completed" => ascending
                ? query.OrderBy(r => r.CompletedAtUtcEpochMs == null)
                       .ThenBy(r => r.CompletedAtUtcEpochMs)
                       .ThenByDescending(r => r.CreatedAtUtcEpochMs)
                : query.OrderBy(r => r.CompletedAtUtcEpochMs == null)
                       .ThenByDescending(r => r.CompletedAtUtcEpochMs)
                       .ThenByDescending(r => r.CreatedAtUtcEpochMs),
            "status" => ascending
                ? query.OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAtUtcEpochMs)
                : query.OrderByDescending(r => r.Status).ThenByDescending(r => r.CreatedAtUtcEpochMs),
            "subject" => ascending
                ? query.OrderBy(r => r.Subject).ThenByDescending(r => r.CreatedAtUtcEpochMs)
                : query.OrderByDescending(r => r.Subject).ThenByDescending(r => r.CreatedAtUtcEpochMs),
            _ /* dispatched */ => ascending
                ? query.OrderBy(r => r.CreatedAtUtcEpochMs)
                : query.OrderByDescending(r => r.CreatedAtUtcEpochMs),
        };

        // v1.3 #158: window with Skip+Take instead of the legacy hard cap. The Include
        // calls hang off the windowed query so we only hydrate Recipients +
        // DocumentTemplate for the rows we're returning.
        var skip = (effectivePage - 1) * effectivePageSize;
        var requests = await orderedQuery
            .Skip(skip)
            .Take(effectivePageSize)
            .Include(r => r.Recipients).ThenInclude(p => p.Role)
            .Include(r => r.DocumentTemplate)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Project after materialization so the ToResponse helper (which composes URLs
        // through WorkflowEmailOptions and HttpContext) can do its job without query-tree
        // translation hassles.
        var rows = requests.Select(r => new
        {
            id = r.Id,
            templateId = r.DocumentTemplateId,
            templateName = r.DocumentTemplate?.Name,
            subject = r.Subject,
            status = r.Status,
            createdAtUtc = r.CreatedAtUtc,
            completedAtUtc = r.CompletedAtUtc,
            recipients = ToResponse(r, http, emailOptions).Recipients,
        }).ToList();

        // totalPages is at least 1 even for empty result sets, so UI math like
        // "Page X of Y" never renders a degenerate "Page 1 of 0".
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)effectivePageSize);

        return Results.Ok(new
        {
            items = rows,
            total,
            page = effectivePage,
            pageSize = effectivePageSize,
            totalPages,
        });
    }

    private static SigningRequestResponse ToResponse(
        SigningRequest request,
        HttpContext http,
        WorkflowEmailOptions emailOptions)
    {
        // When SigningUrlTemplate is configured (typical when a Blazor UI hosts the
        // recipient page on a different origin than the WebApi), use it. Otherwise fall
        // back to the WebApi's own /api/sign/{token} route.
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";

        string BuildAccessUrl(string token) =>
            string.IsNullOrWhiteSpace(emailOptions.SigningUrlTemplate)
                ? $"{baseUrl}/api/sign/{token}"
                : emailOptions.SigningUrlTemplate!.Replace("{accessToken}", token, StringComparison.Ordinal);

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
                    : BuildAccessUrl(r.AccessToken)))
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
