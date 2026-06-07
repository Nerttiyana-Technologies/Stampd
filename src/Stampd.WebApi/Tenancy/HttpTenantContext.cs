using System.Globalization;
using Microsoft.Extensions.Primitives;
using Stampd.Core.Tenancy;

namespace Stampd.WebApi.Tenancy;

/// <summary>
/// Reads the active tenant from the <c>X-Stampd-Tenant</c> HTTP header. Falls back to a
/// configured default if the header is absent or unparseable. Registered as scoped so
/// the DbContext sees the right tenant per request.
/// </summary>
/// <remarks>
/// This is the v1 multi-tenant primitive — enough for dev and demos where the caller knows
/// the tenant id. Production should replace this with one that reads the tenant from the
/// authenticated principal's claims, after AuthN/AuthZ is wired in.
/// </remarks>
public sealed class HttpTenantContext : ITenantContext
{
    private const string HeaderName = "X-Stampd-Tenant";

    public HttpTenantContext(IHttpContextAccessor accessor, Guid defaultTenantId)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        TenantId = ResolveTenant(accessor.HttpContext, defaultTenantId);
    }

    public Guid TenantId { get; }

    private static Guid ResolveTenant(HttpContext? context, Guid fallback)
    {
        // Background-worker override takes precedence: if a TenantScope is active on this
        // async flow, use it. This lets BulkSendWorker (and tests) switch tenants without
        // owning the DI registration.
        if (TenantScope.Current is { } scoped)
        {
            return scoped;
        }

        if (context is null)
        {
            // Background work outside a request — use the configured default.
            return fallback;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out StringValues values) || values.Count == 0)
        {
            return fallback;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return Guid.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }
}
