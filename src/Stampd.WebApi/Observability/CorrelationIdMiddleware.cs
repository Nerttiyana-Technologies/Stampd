using Serilog.Context;

namespace Stampd.WebApi.Observability;

/// <summary>
/// Reads <c>X-Correlation-Id</c> from the incoming request (generating one if absent),
/// pushes it into Serilog's LogContext for the duration of the request, and echoes it
/// back on the response. Every log line within the request shares the same id, so you
/// can grep a tail across services.
/// </summary>
internal sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied)
                && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = correlationId;
        context.Items[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}
