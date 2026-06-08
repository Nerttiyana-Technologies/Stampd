using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

/// <summary>
/// v2.3 #232 — audit-trail CSV export. Compliance teams' top ask: a single download
/// they can hand to auditors or pipe into their SIEM, instead of pagination-walking
/// the JSON audit API. Streamed response so the WebApi doesn't materialize the
/// entire result set in memory — a year-of-data export stays bounded by the
/// network's send buffer.
/// </summary>
/// <remarks>
/// Lives under the same <c>/api/admin</c> Admin-only group as the other admin
/// endpoints. The endpoint accepts date-range + event-type + actor filters and
/// returns <c>text/csv</c> with a <c>Content-Disposition: attachment</c> header so
/// browsers prompt to save rather than render inline.
/// </remarks>
internal static class AdminAuditExportEndpoints
{
    /// <summary>
    /// Hard cap on rows per export. Streamed responses don't need this for memory
    /// reasons, but a 100M-row blob still needs a humane stopping point — we want
    /// adopters to page their exports rather than wait 20 minutes for one download.
    /// Configurable via <c>Stampd:Admin:MaxAuditExportRows</c>.
    /// </summary>
    public const int DefaultMaxRows = 250_000;

    public static IEndpointRouteBuilder MapAdminAuditExport(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/audit").WithTags("AdminAudit");

        group.MapGet("/export", ExportAsync)
            .WithName("ExportAdminAudit")
            .WithSummary("Stream the audit trail as CSV. Optional from/to/eventType/actorUserId filters. v2.3 #232.");

        return builder;
    }

    private static async Task<IResult> ExportAsync(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? eventType,
        [FromQuery] string? actorUserId,
        [FromQuery] int? maxRows,
        [FromServices] StampdDbContext db,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        // Configurable cap so adopters with adoption-scale data can override without
        // recompiling. Hard-clamped below to keep one bad caller from spinning up
        // an unbounded query plan.
        var configuredMax = config.GetValue("Stampd:Admin:MaxAuditExportRows", defaultValue: DefaultMaxRows);
        var rowCap = Math.Clamp(maxRows ?? configuredMax, 1, configuredMax);

        // Build the EF query. All filters are optional; missing ones widen the
        // selection. We don't enforce a tenant filter explicitly — the global query
        // filter on AuditEvent in StampdDbContext does that for us based on the
        // tenant header context.
        var query = db.AuditEvents.AsQueryable();

        if (from is not null)
        {
            query = query.Where(e => e.OccurredAtUtc >= from.Value);
        }
        if (to is not null)
        {
            query = query.Where(e => e.OccurredAtUtc <= to.Value);
        }
        if (!string.IsNullOrWhiteSpace(eventType) &&
            Enum.TryParse<AuditEventType>(eventType, ignoreCase: true, out var parsedEventType))
        {
            query = query.Where(e => e.EventType == parsedEventType);
        }
        if (!string.IsNullOrWhiteSpace(actorUserId))
        {
            var actor = actorUserId.Trim();
            query = query.Where(e => e.ActorUserId == actor);
        }

        // Order oldest-first for chronological export. Auditors expect time-ordered
        // rows; the OccurredAtUtc index makes this index-covered.
        query = query.OrderBy(e => e.OccurredAtUtc).Take(rowCap);

        // Compose the filename. Encoding the filter bounds makes the saved file
        // self-describing — auditors don't have to remember what window each
        // file covered.
        var fromStamp = from?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "all";
        var toStamp = to?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "now";
        var filename = $"stampd-audit-{fromStamp}-{toStamp}.csv";

        // Results.Stream's callback overload takes Func<Stream, Task> — one
        // parameter. We close over `query` and `ct` so the writer has everything
        // it needs without a second positional argument.
        return Results.Stream(
            stream => WriteCsvAsync(query, stream, ct),
            contentType: "text/csv; charset=utf-8",
            fileDownloadName: filename);
    }

    /// <summary>
    /// Stream rows directly into the response body. We use a thin
    /// <see cref="StreamWriter"/> wrapper for newline handling and rely on the
    /// query's deferred execution (no <c>.ToList</c>) so EF Core streams rows
    /// straight from the database cursor — memory stays bounded regardless of
    /// row count.
    /// </summary>
    private static async Task WriteCsvAsync(IQueryable<AuditEvent> query, Stream output, CancellationToken ct)
    {
        // Leave-open + explicit flush so the caller's stream lifecycle stays sane.
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);

        // RFC 4180 — header row first. PayloadJson goes last because it's the
        // widest column and pushing it right keeps narrow columns scannable when
        // the file is opened in a spreadsheet.
        await writer.WriteLineAsync(string.Join(',', new[]
        {
            "Id",
            "OccurredAtUtc",
            "EventType",
            "SigningRequestId",
            "RecipientId",
            "ActorUserId",
            "ActorRole",
            "IpAddress",
            "UserAgent",
            "GeoCountry",
            "GeoCity",
            "DocumentHashAtEvent",
            "IsRedacted",
            "PayloadJson",
        })).ConfigureAwait(false);

        await foreach (var e in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            var line = string.Join(',', new[]
            {
                CsvField(e.Id.ToString()),
                CsvField(e.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                CsvField(e.EventType.ToString()),
                CsvField(e.SigningRequestId.ToString()),
                CsvField(e.RecipientId?.ToString() ?? string.Empty),
                CsvField(e.ActorUserId ?? string.Empty),
                CsvField(e.ActorRole ?? string.Empty),
                CsvField(e.IpAddress ?? string.Empty),
                CsvField(e.UserAgent ?? string.Empty),
                CsvField(e.GeoCountry ?? string.Empty),
                CsvField(e.GeoCity ?? string.Empty),
                CsvField(e.DocumentHashAtEvent ?? string.Empty),
                CsvField(e.IsRedacted ? "true" : "false"),
                CsvField(e.PayloadJson ?? string.Empty),
            });
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RFC 4180-ish escaping. We always quote and double internal quotes — simpler
    /// and safer than conditional quoting (no commas / quotes / newlines in raw
    /// fields rule). Excel and pandas both round-trip this correctly.
    /// </summary>
    private static string CsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
