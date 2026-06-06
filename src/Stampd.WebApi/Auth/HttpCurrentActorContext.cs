using System.Security.Claims;

using Microsoft.IdentityModel.JsonWebTokens;

using Stampd.Core.Authorization;

namespace Stampd.WebApi.Auth;

/// <summary>
/// v2.0 Slice D — <see cref="ICurrentActorContext"/> implementation that reads from the
/// authenticated <see cref="ClaimsPrincipal"/> on the active HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// Scoped lifetime because <see cref="IHttpContextAccessor"/> is per-request and the
/// underlying ClaimsPrincipal is mutated by ASP.NET Core's auth middleware mid-pipeline
/// — we want a fresh read on every resolve.
/// </para>
/// <para>
/// Falls back gracefully when there's no HttpContext (background workers, hosted
/// services, integration tests that bypass the pipeline). All four members return safe
/// defaults (<c>null</c>, <c>false</c>) so callers don't need to null-check the context
/// itself.
/// </para>
/// </remarks>
internal sealed class HttpCurrentActorContext : ICurrentActorContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentActorContext(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? UserId
    {
        get
        {
            var principal = Principal;
            if (principal is null) return null;

            // JWT bearer middleware maps the "sub" claim to ClaimTypes.NameIdentifier
            // unless MapInboundClaims is false. We check both to survive either
            // configuration without forcing the WebApi to call
            // JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear() globally.
            return principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        }
    }

    public string? ActiveRole
    {
        get
        {
            var principal = Principal;
            if (principal is null) return null;

            // Pick the most-privileged role the caller holds. Admin > Sender > ReadOnly.
            // A caller carrying multiple role claims should be reported as the highest
            // tier — that's the role they're effectively acting in when they hit an
            // admin-gated endpoint.
            if (principal.IsInRole(StampdRoles.Admin)) return StampdRoles.Admin;
            if (principal.IsInRole(StampdRoles.Sender)) return StampdRoles.Sender;
            if (principal.IsInRole(StampdRoles.ReadOnly)) return StampdRoles.ReadOnly;
            return null;
        }
    }

    public bool IsAdmin => string.Equals(ActiveRole, StampdRoles.Admin, StringComparison.Ordinal);
}
